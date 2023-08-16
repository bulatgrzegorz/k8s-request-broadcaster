# K8s Request Broadcaster

Example of .NET Web Api that allowing to broadcast received request to all pods returned by given service ([headless](https://kubernetes.io/docs/concepts/services-networking/service/#headless-services)).

# Usage

Api is declaring endpoint with signature:
```csharp
app.Map("/{serviceName}/{targetPort}/{*rest}", async (string serviceName, int targetPort, string? rest) => { });
```

It is expecting service name of headless-service that should be use in order to find all pods that request should broadcast to, and target port on which pods are listening in.

It will match URL's like:
* `/headless-service/80`\
serviceName = `headless-service`, targetPort = 80
* `/service-name/8888/api/v2/cache-invalidation`\
serviceName = `service2`, targetPort = 8888, rest = `api/v2/cache-invalidation`

It will use same http method as it received and will broadcast exact copy of body and headers (with some exceptions - like Host header). 
