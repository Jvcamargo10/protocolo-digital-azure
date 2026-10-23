using Azure.Data.Tables;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProtocoloDigital.Core.Dominio;
using ProtocoloDigital.Core.Infra;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

var config = builder.Configuration;
var credencial = new DefaultAzureCredential();

// Em produção não há connection string: a Function usa a identidade gerenciada (Managed Identity) e as
// permissões vêm de role assignments no Bicep. Localmente, usa as connection strings do Azurite e do emulador.
builder.Services.AddSingleton(_ => config["AzureWebJobsStorage"] is { Length: > 0 } local
    ? new TableServiceClient(local)
    : new TableServiceClient(new Uri(config["AzureWebJobsStorage:tableServiceUri"]!), credencial));
builder.Services.AddSingleton(_ => config["AzureWebJobsStorage"] is { Length: > 0 } local
    ? new BlobServiceClient(local)
    : new BlobServiceClient(new Uri(config["AzureWebJobsStorage:blobServiceUri"]!), credencial));
builder.Services.AddSingleton(_ => config["ServiceBus"] is { Length: > 0 } local
    ? new ServiceBusClient(local)
    : new ServiceBusClient(config["ServiceBus:fullyQualifiedNamespace"], credencial));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TabelaProtocolos>();
builder.Services.AddSingleton<IRepositorioProtocolos>(sp => sp.GetRequiredService<TabelaProtocolos>());
builder.Services.AddSingleton<ArquivosBlob>();
builder.Services.AddSingleton<IArquivos>(sp => sp.GetRequiredService<ArquivosBlob>());
builder.Services.AddSingleton<IMensageria, MensageriaServiceBus>();
builder.Services.AddSingleton<ServicoDeProtocolos>();
builder.Services.AddHostedService<Inicializacao>();

builder.Build().Run();

/// <summary>Cria tabelas e container na subida (idempotente).</summary>
internal sealed class Inicializacao(TabelaProtocolos tabelas, ArquivosBlob arquivos) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await tabelas.CriarTabelasAsync(ct);
        await arquivos.CriarContainerAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
