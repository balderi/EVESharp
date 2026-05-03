using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using EVESharp.EVE.Network.Services.Exceptions;
using EVESharp.EVE.Network.Services.Validators;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.EVE.Network.Services;

public abstract class Service
{
    public abstract AccessLevel AccessLevel { get; }

    public string Name => this.GetType().Name;

    private bool FindSuitableMethod(string methodName, ServiceCall extra, out object[] parameters, out MethodInfo matchingMethod)
    {
        PyTuple      arguments      = extra.Payload;
        PyDictionary namedArguments = extra.NamedPayload;
        IEnumerable<MethodInfo> methods = this
                                          .GetType()
                                          .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                          .Where(x => x.Name == methodName)
                                          .OrderBy (x => x.GetParameters ().Length);

        matchingMethod = null;
        parameters     = null;
            

Console.WriteLine($"[DEBUG] ExecuteCall {this.GetType().FullName}::{methodName} payloadCount={arguments.Count}");
for (int i = 0; i < arguments.Count; i++)
{
    string t = arguments[i]?.GetType().Name ?? "null";
    Console.WriteLine($"[DEBUG]   arg[{i}] type={t}");
}

            
       foreach (MethodInfo method in methods)
{
    Console.WriteLine($"[DEBUG] Checking method {method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))}) for call {methodName}");

    ParameterInfo[] methodParameters = method.GetParameters();

    // Skip if argument count doesn't match (accounting for ServiceCall)
    int required = methodParameters.Length - 1; // exclude the implicit ServiceCall
int actual   = arguments.Count;

// Allow extra trailing "dummy" args (null/PyNone) to be ignored for zero-arg methods.
bool extraAreIgnorable = false;
if (actual > required)
{
    // if all extra args are null or PyNone, we can safely ignore them
    extraAreIgnorable = true;
    for (int i = required; i < actual; i++)
    {
        PyDataType el = arguments[i];
        if (el != null && el.GetType().Name != "PyNone")
        {
            extraAreIgnorable = false;
            break;
        }
    }
}

// original strict check becomes: must match exactly OR be a zero-arg method with ignorable extras
if (actual != required && !(required == 0 && extraAreIgnorable))
    continue;


    // Special case: only ServiceCall
    if (methodParameters.Length == 1)
    {
        matchingMethod = method;
        parameters = new object[] { extra };
        Console.WriteLine($"[DEBUG] Matched simple method {method.Name} with only ServiceCall param");
        return true;
    }

    parameters = new object[methodParameters.Length];
    parameters[0] = extra;

    bool match = true;

    for (int parameterIndex = 1, argumentIndex = 0; parameterIndex < methodParameters.Length; parameterIndex++, argumentIndex++)
    {
        if (argumentIndex >= arguments.Count)
        {
            if (namedArguments.TryGetValue(methodParameters[parameterIndex].Name, out PyDataType value))
            {
                parameters[parameterIndex] = value;
                match = true;
                break;
            }
            if (methodParameters[parameterIndex].IsOptional == false)
            {
                match = false;
                break;
            }

            parameters[parameterIndex] = methodParameters[parameterIndex].DefaultValue;
        }
        else
        {
            PyDataType element = arguments[argumentIndex];

            if (element is null || methodParameters[parameterIndex].IsOptional)
                parameters[parameterIndex] = null;
            else if (methodParameters[parameterIndex].ParameterType == element.GetType() ||
                     methodParameters[parameterIndex].ParameterType == element.GetType().BaseType)
                parameters[parameterIndex] = element;
            else
            {
                match = false;
                break;
            }
        }
    }

    if (match)
    {
        matchingMethod = method;
        return true;
    }
}

return false;



    }

    public PyDataType ExecuteCall(string method, ServiceCall extraInformation)
    {
        if (this.FindSuitableMethod(method, extraInformation, out object[] parameters, out MethodInfo methodInfo) == false)
        {
        
            Console.WriteLine($"[Service.ExecuteCall] MISSING method '{method}' on service '{this.GetType().FullName}'");
            throw new MissingCallException(Name, method);
        }

        List <CallValidator> requirements = this.GetType ().GetCustomAttributes <CallValidator> ().Concat (methodInfo.GetCustomAttributes <CallValidator> ()).ToList ();

        if (requirements.Any ())
        {
            foreach (CallValidator validator in requirements)
            {
                if (validator.Validate (extraInformation.Session) == false)
                {
                    if (validator.Exception is not null)
                        throw (Exception) Activator.CreateInstance (validator.Exception, validator.ExceptionParameters);

                    return null;
                }
            }
        }

        try
        {
            return (PyDataType) methodInfo.Invoke(this, parameters);
        }
        catch (TargetInvocationException e)
        {
            if (e.InnerException is not null)
                ExceptionDispatchInfo.Throw(e.InnerException);
            throw;
        }
    }
}
