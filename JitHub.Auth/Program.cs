using Microsoft.Extensions.Hosting;
using ThrottlingTroll;

bool isDebugMode = false;
#if DEBUG
isDebugMode = true;
#endif

var limit = isDebugMode ? 1000 : 10;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults((hostBuilderContext, workerAppBuilder) => {

        workerAppBuilder.UseThrottlingTroll(hostBuilderContext, options =>
        {
            options.Config = new ThrottlingTrollConfig
            {
                Rules = new[]
                {
                    new ThrottlingTrollRule
                    {
                        UriPattern = "/api",
                        LimitMethod = new FixedWindowRateLimitMethod
                        {
                            PermitLimit = limit,
                            IntervalInSeconds = 3600
                        }
                    },
                    // add more rules here...
                }
            };
        });
    })
    .Build();

host.Run();
