using System;
using System.Data;

namespace EVESharp.Database;

public class DbLock : IDisposable
{
    /// <summary>
    /// The actual database connection
    /// </summary>
    public IDbConnection Connection { get; init; }

    /// <summary>
    /// The <see cref="IDatabase"/> that created the lock
    /// </summary>
    public IDatabase Creator { get; init; }

    /// <summary>
    /// The name of the acquired lock
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Indicates if this DatabaseLock was disposed already
    /// </summary>
    private bool mDisposed = false;

    public void Dispose ()
    {
        if (this.mDisposed == true)
            return;

        this.mDisposed = true;
        
        if (Connection is null)
            return;

        if (Connection.State == ConnectionState.Closed)
        {
            Connection.Dispose ();
            return;
        }

        // release the lock
        Creator.ReleaseLock (this);
        // dispose of the connection
        Connection.Dispose ();
    }
}