// =====================================================================
// Lanceur de développement NetPrint (Aspire 13 : SDK .NET 10 + Visual Studio 2026)
// Une seule application à lancer : NetPrint.Web (tous les modules sont dedans).
// Plus tard : PostgreSQL (builder.AddPostgres) quand un module basculera sur PostgreSQL.
// =====================================================================

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.NetPrint_Web>("netprint-web");

builder.Build().Run();
