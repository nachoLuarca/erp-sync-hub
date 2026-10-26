using ErpSyncHub.Application.Inventory;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ErpSyncHub.Api.Inventory;

internal static class InventorySyncServiceCollectionExtensions
{
    /// <summary>
    /// Registra la configuración de la sincronización de stock y precios y el servicio que la dispara periódicamente. La
    /// configuración se valida al iniciar la aplicación.
    /// </summary>
    public static IServiceCollection AddInventorySync(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<InventorySyncOptions>()
            .Bind(configuration.GetSection(InventorySyncOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<InventorySyncOptions>, InventorySyncOptionsValidator>());
        services.AddSingleton(serviceProvider => serviceProvider.GetRequiredService<IOptions<InventorySyncOptions>>().Value);

        // Va después de la migración de la base de datos (registrada en AddInfrastructure): los servicios se inician en orden.
        services.AddHostedService<InventorySyncWorker>();

        return services;
    }
}

internal sealed class InventorySyncOptionsValidator : IValidateOptions<InventorySyncOptions>
{
    /// <summary>El código de almacén de SAP (<c>WhsCode</c>) admite hasta 8 caracteres.</summary>
    private const int MaxWarehouseCodeLength = 8;

    public ValidateOptionsResult Validate(string? name, InventorySyncOptions options)
    {
        var errors = new List<string>();

        if (options.Interval < TimeSpan.FromSeconds(1) || options.Interval > TimeSpan.FromHours(24))
        {
            errors.Add("Sync:Inventory:Interval debe estar entre 1 segundo y 24 horas (variable de entorno Sync__Inventory__Interval, por ejemplo 00:15:00).");
        }

        var warehouses = options.WarehouseCodes;
        if (warehouses.Count == 0 || warehouses.Any(code => code.Length > MaxWarehouseCodeLength))
        {
            errors.Add(
                $"Sync:Inventory:Warehouses debe listar al menos un código de almacén de SAP, de hasta {MaxWarehouseCodeLength} caracteres, " +
                "separados por comas (variable de entorno Sync__Inventory__Warehouses, por ejemplo 01,02).");
        }

        if (options.PriceList < 1)
        {
            errors.Add("Sync:Inventory:PriceList debe ser el número de una lista de precios de SAP, desde 1 (variable de entorno Sync__Inventory__PriceList).");
        }

        if (options.PriceDecimals is < 0 or > 4)
        {
            errors.Add("Sync:Inventory:PriceDecimals debe estar entre 0 y 4 (variable de entorno Sync__Inventory__PriceDecimals).");
        }

        if (options.SapPageSize is < 1 or > 1000)
        {
            errors.Add("Sync:Inventory:SapPageSize debe estar entre 1 y 1000 (variable de entorno Sync__Inventory__SapPageSize).");
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            errors.Add(
                $"Sync:Inventory:TimeZoneId '{options.TimeZoneId}' no es una zona horaria válida (use un identificador IANA, por ejemplo America/Santiago).");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
