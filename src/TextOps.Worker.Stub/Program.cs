// Worker.Stub is now integrated into DevApi as a service
// This Program.cs is kept for potential standalone worker scenarios
// For DevApi integration, the worker executor is registered in DevApi's Program.cs

var builder = Host.CreateApplicationBuilder(args);

var host = builder.Build();
host.Run();
