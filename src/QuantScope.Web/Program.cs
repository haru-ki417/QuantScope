using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using QuantScope.Web;
using QuantScope.Web.State;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.Services.AddSingleton<Workspace>();
builder.Services.AddSingleton<BrowserIo>();
await builder.Build().RunAsync();
