namespace prop.Web

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Serilog
open System
open prop

/// Equinox store bindings
module Storage =
    /// Specifies the store to be used, together with any relevant custom parameters
    [<RequireQualifiedAccess>]
    type Config =
        | Mem
        | Cosmos of mode: Equinox.Cosmos.ConnectionMode * connectionStringWithUriAndKey: string * database: string * container: string * cacheMb: int

    /// Holds an initialized/customized/configured of the store as defined by the `Config`
    type Instance =
        | MemoryStore of Equinox.MemoryStore.VolatileStore<obj>
        | CosmosStore of store: Equinox.Cosmos.Context * cache: Equinox.Cache

    /// MemoryStore 'wiring', uses Equinox.MemoryStore nuget package
    module private Memory =
        open Equinox.MemoryStore
        let connect () =
            VolatileStore()

    /// CosmosDb wiring, uses Equinox.Cosmos nuget package
    module private Cosmos =
        open Equinox.Cosmos
        let connect appName (mode, discovery) (operationTimeout, maxRetryForThrottling, maxRetryWait) =
            let log = Log.ForContext<Instance>()
            let c = Connector(log=log, mode=mode, requestTimeout=operationTimeout, maxRetryAttemptsOnRateLimitedRequests=maxRetryForThrottling, maxRetryWaitTimeOnRateLimitedRequests=maxRetryWait)
            let conn = c.Connect(appName, discovery) |> Async.RunSynchronously
            Gateway(conn, BatchingPolicy(defaultMaxItems=500))

    /// Creates and/or connects to a specific store as dictated by the specified config
    let connect : Config -> Instance = function
        | Config.Mem ->
            let store = Memory.connect()
            Instance.MemoryStore store
        | Config.Cosmos (mode, connectionString, database, container, cache) ->
            let cache = Equinox.Cache("Cosmos", sizeMb=10)
            let retriesOn429Throttling = 1 // Number of retries before failing processing when provisioned RU/s limit in CosmosDb is breached
            let timeout = TimeSpan.FromSeconds 5. // Timeout applied per request to CosmosDb, including retry attempts
            let gateway = Cosmos.connect "App" (mode, Equinox.Cosmos.Discovery.FromConnectionString connectionString) (timeout, retriesOn429Throttling, timeout)
            let containers = Equinox.Cosmos.Containers(database, container)
            let context = Equinox.Cosmos.Context(gateway, containers)
            Instance.CosmosStore (context, cache)

/// Dependency Injection wiring for services using Equinox
module Services =
    /// Builds a Stream Resolve function appropriate to the store being used
    type StreamResolver(storage : Storage.Instance) =
        member __.Resolve
            (   codec : FsCodec.IEventCodec<'event, byte[], _>,
                fold: ('state -> 'event seq -> 'state),
                initial: 'state,
                snapshot: (('event -> bool) * ('state -> 'event))) =
            match storage with
            | Storage.MemoryStore store ->
                Equinox.MemoryStore.Resolver(store, FsCodec.Box.Codec.Create(), fold, initial).Resolve
            | Storage.CosmosStore (store, cache) ->
                let accessStrategy = Equinox.Cosmos.AccessStrategy.Snapshot snapshot
                let cacheStrategy = Equinox.Cosmos.CachingStrategy.SlidingWindow (cache, TimeSpan.FromMinutes 20.)
                Equinox.Cosmos.Resolver<'event, 'state, _>(store, codec, fold, initial, cacheStrategy, accessStrategy).Resolve

    /// Binds a storage independent Service's Handler's `resolve` function to a given Stream Policy using the StreamResolver
    type ServiceBuilder(resolver: StreamResolver) =
         member __.CreateTodosService() =
            let fold, initial = Todo.Fold.fold, Todo.Fold.initial
            let snapshot = Todo.Fold.isOrigin, Todo.Fold.snapshot
            Todo.create (resolver.Resolve(Todo.Events.codec, fold, initial, snapshot))
         member __.CreateAggregateService() =
            let fold, initial = Aggregate.Fold.fold, Aggregate.Fold.initial
            let snapshot = Aggregate.Fold.isOrigin, Aggregate.Fold.snapshot
            Aggregate.create (resolver.Resolve(Aggregate.Events.codec, fold, initial, snapshot))

    /// F# syntactic sugar for registering services
    type IServiceCollection with
        /// Register a Service as a Singleton, by supplying a function that can build an instance of the type in question
        member services.Register(factory : IServiceProvider -> 'T) = services.AddSingleton<'T>(fun (sp: IServiceProvider) -> factory sp) |> ignore
    
    /// F# syntactic sugar to resolve a service dependency
    type IServiceProvider with member sp.Resolve<'t>() = sp.GetRequiredService<'t>()

    /// Registers the Equinox Store, Stream Resolver, Service Builder and the Service
    let register (services : IServiceCollection, storeCfg) =
        services.Register(fun _sp -> Storage.connect storeCfg)
        services.Register(fun sp -> StreamResolver(sp.Resolve()))
        services.Register(fun sp -> ServiceBuilder(sp.Resolve()))
        services.Register(fun sp -> sp.Resolve<ServiceBuilder>().CreateTodosService())
        services.Register(fun sp -> sp.Resolve<ServiceBuilder>().CreateAggregateService())

/// Defines the Hosting configuration, including registration of the store and backend services
type Startup() =
    // This method gets called by the runtime. Use this method to add services to the container.
    member __.ConfigureServices(services: IServiceCollection) : unit =
        services
            .AddMvc()
            .SetCompatibilityVersion(CompatibilityVersion.Latest)
            .AddNewtonsoftJson() // until FsCodec.SystemTextJson is available
            |> ignore

        // This is the allocation limit passed internally to a System.Caching.MemoryCache instance
        // The primary objects held in the cache are the Folded State of Event-sourced aggregates
        // see https://docs.microsoft.com/en-us/dotnet/framework/performance/caching-in-net-framework-applications for more information
        let cacheMb = 50

        // AZURE COSMOSDB: Events are stored in an Azure CosmosDb Account (using the SQL API)
        // Provisioning Steps:
        // 1) Set the 3x environment variables EQUINOX_COSMOS_CONNECTION, EQUINOX_COSMOS_DATABASE, EQUINOX_COSMOS_CONTAINER
        // 2) Provision a container using the following command sequence:
        //     dotnet tool install -g Equinox.Tool
        //     eqx init -ru 1000 cosmos -s $env:EQUINOX_COSMOS_CONNECTION -d $env:EQUINOX_COSMOS_DATABASE -c $env:EQUINOX_COSMOS_CONTAINER
        let storeConfig = 
            let connectionVar, databaseVar, containerVar = "EQUINOX_COSMOS_CONNECTION", "EQUINOX_COSMOS_DATABASE", "EQUINOX_COSMOS_CONTAINER"
            let read key = Environment.GetEnvironmentVariable key |> Option.ofObj
            match read connectionVar, read databaseVar, read containerVar with
            | Some connection, Some database, Some container ->
                let connMode = Equinox.Cosmos.ConnectionMode.Direct // Best perf - select one of the others iff using .NETCore on linux or encounter firewall issues
                Storage.Config.Cosmos (connMode, connection, database, container, cacheMb) 
            | None, Some database, Some container ->
                // alternately, you can feed in this connection string in as a parameter externally and remove this special casing
                let wellKnownConnectionStringForCosmosDbSimulator =
                    "AccountEndpoint=https://localhost:8081;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;"
                Storage.Config.Cosmos (Equinox.Cosmos.ConnectionMode.Direct, wellKnownConnectionStringForCosmosDbSimulator, database, container, cacheMb)
            | _ ->
                failwithf "Event Storage subsystem requires the following Environment Variables to be specified: %s, %s, %s" connectionVar databaseVar containerVar

        Services.register(services, storeConfig)

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    member __.Configure(app: IApplicationBuilder, env: IHostEnvironment) : unit =
        if env.IsDevelopment() then app.UseDeveloperExceptionPage() |> ignore
        else app.UseHsts() |> ignore

        app.UseHttpsRedirection()
            .UseRouting()
            .UseSerilogRequestLogging() // see https://nblumhardt.com/2019/10/serilog-in-aspnetcore-3/
            // NB Jet does now own, control or audit https://todobackend.com; it is a third party site; please satisfy yourself that this is a safe thing use in your environment before using it._
            .UseCors(fun x -> x.WithOrigins([|"*"|]).AllowAnyHeader().AllowAnyMethod() |> ignore)
            .UseEndpoints(fun endpoints -> endpoints.MapControllers() |> ignore)
            |> ignore