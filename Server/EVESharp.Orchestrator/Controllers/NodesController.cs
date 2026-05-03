using EVESharp.Database;
using EVESharp.Orchestrator.Models;
using EVESharp.Orchestrator.Providers;
using EVESharp.Orchestrator.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace EVESharp.Orchestrator.Controllers;

[ApiController]
[Route ("[controller]")]
public class NodesController : ControllerBase
{
    private IDatabase      DB                  { get; }
    private ILogger<NodesController> Logger              { get; }
    private IConfiguration           Configuration       { get; }
    private IStartupInfoProvider     StartupInfoProvider { get; }
    private IClusterRepository       ClusterRepository   { get; }

    public NodesController (
        IDatabase  db,
        ILogger<NodesController>      logger,
        IStartupInfoProvider startupInfoProvider,
        IConfiguration       configuration,
        IClusterRepository   clusterRepository)
    {
        DB                  = db;
        Logger              = logger;
        Configuration       = configuration;
        StartupInfoProvider = startupInfoProvider;
        ClusterRepository   = clusterRepository;
    }

    [HttpGet (Name = "GetNodeList")]
    [Produces ("application/json")]
    public ActionResult <IEnumerable <Node>> GetNodeList ()
    {
        return ClusterRepository.FindNodes ();
    }

    [HttpGet ("{address}")]
    [Produces ("application/json")]
    public ActionResult <Node> GetNodeByAddress (string address)
    {
        try
        {
            return ClusterRepository.FindByAddress (address);
        }
        catch (InvalidDataException e)
        {
            return this.NotFound (e.Message);
        }
    }

    [HttpGet ("node/{nodeId}")]
    [Produces ("application/json")]
    public ActionResult <Node> GetNodeInformation (int nodeId)
    {
        try
        {
            return ClusterRepository.FindById (nodeId);
        }
        catch (InvalidDataException e)
        {
            return this.NotFound (e.Message);
        }
    }

    [HttpGet ("proxies")]
    [Produces ("application/json")]
    public ActionResult <List <Node>> GetProxies ()
    {
        return ClusterRepository.FindProxyNodes ();
    }

    [HttpGet ("servers")]
    [Produces ("application/json")]
    public ActionResult <List <Node>> GetServers ()
    {
        return ClusterRepository.FindServerNodes ();
    }

    [HttpPost ("register")]
    [Produces ("application/json")]
    public ActionResult <object> RegisterNewNode ([FromForm] ushort port, [FromForm] string role)
    {
        if (role != "proxy" && role != "server")
            return this.BadRequest ($"Unknown node role... {role}");

        Node newNode = ClusterRepository.RegisterNode (
            Request.HttpContext.Connection.RemoteIpAddress?.ToString ()!,
            port,
            role
        );
        
        Logger.LogInformation ("Registered a new node with address {newNode.Address}, coming from IP {newNode.IP}", newNode.Address, newNode.Ip);

        return new
        {
            NodeId       = newNode.NodeId,
            Address      = newNode.Address,
            TimeInterval = int.Parse (Configuration.GetSection ("Cluster") ["TimedEventsInterval"]),
            StartupTime  = StartupInfoProvider.Time.ToFileTimeUtc ()
        };
    }

    [HttpPost ("heartbeat")]
    public void DoHeartbeat ([FromForm] string address, [FromForm] float load)
    {
        Logger.LogInformation ("Received heartbeat from {address} with load {load}", address, load);

        ClusterRepository.Hearbeat (address, load);
    }

    [HttpGet ("next")]
    [Produces ("application/json")]
    public ActionResult <long> GetNextNode ()
    {
        try
        {
            Node node = ClusterRepository.GetLeastLoadedNode ();
            
            Logger.LogInformation ("Returned node ({node.NodeID}) with lowest load ({node.Load})", node.NodeId, node.Load);

            return node.NodeId;
        }
        catch (InvalidDataException e)
        {
            return this.NotFound (e.Message);
        }
    }
}