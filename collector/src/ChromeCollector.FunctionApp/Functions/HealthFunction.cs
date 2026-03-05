using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace ChromeCollector.FunctionApp.Functions;

public sealed class HealthFunction
{
    [Function("Health")]
    public HttpResponseData Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData request)
    {
        var response = request.CreateResponse(System.Net.HttpStatusCode.OK);
        response.WriteString("ok");
        return response;
    }
}
