using JitHub.Web;
using JitHub.Web.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
namespace JitHub.Web
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebAssemblyHostBuilder.CreateDefault(args);
            builder.RootComponents.Add<App>("#app");
            builder.RootComponents.Add<HeadOutlet>("head::after");
            Config.API_KEY = "KqMwH3gSmD711FcrnsUQP5aXS1L8ksfDsP_YOdyEAvBdAzFuSTxUjg==";
            builder.Services.AddScoped(sp =>
                new HttpClient { BaseAddress = new Uri(builder.Configuration["API_Prefix"] ?? builder.HostEnvironment.BaseAddress) });
            await builder.Build().RunAsync();
        }
    }
}