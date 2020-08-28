namespace prop.Controllers

open Microsoft.AspNetCore.Mvc
open DirectIngestor

[<Route "[controller]"; ApiController>]
type TodosSummaryController(service: TodoSummary.Service) =
    inherit ControllerBase()
   
    [<HttpGet>]
    member __.Get([<FromClientIdHeader>]clientId : ClientId) = async {
        let! xs = service.Read clientId
        return ObjectResult(xs)
    }
