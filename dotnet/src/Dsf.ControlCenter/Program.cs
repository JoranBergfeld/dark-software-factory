using Dsf.Core;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => $"{CoreModule.Name} :: Dsf.ControlCenter skeleton");

app.Run();
