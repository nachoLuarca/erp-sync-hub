using ErpSyncHub.Application.Common;
using ErpSyncHub.Application.Jobs;
using ErpSyncHub.Application.Sap;
using ErpSyncHub.Application.WooCommerce;
using ErpSyncHub.Domain.Jobs;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ErpSyncHub.Api.Logging;

/// <summary>
/// La política de logs para datos personales: ningún campo personal llega a la consola ni a Seq, aunque alguien lo agregue a un
/// mensaje sin pensarlo. Combina un destructurador (un objeto del pedido nunca se desarma en sus campos) con un enriquecedor que,
/// antes de que el evento llegue a un destino, oculta las propiedades con nombre de dato personal, los objetos del pedido
/// escritos con <c>ToString</c> y, en cualquier texto, los RUT, los correos y los datos del pedido en proceso.
/// </summary>
internal static class PersonalDataLogging
{
    /// <summary>Lo que reemplaza a un campo personal.</summary>
    public const string Hidden = "[personal]";

    /// <summary>Nombres de propiedad que contienen datos personales (sin distinguir mayúsculas).</summary>
    private static readonly HashSet<string> PersonalProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Email", "EmailAddress", "Phone", "Telephone", "Rut", "TaxId", "FederalTaxID", "FirstName", "LastName", "FullName", "CardName",
        "Address", "Address1", "Address2", "Billing", "Shipping", "Payload", "CustomerNote", "Company",
    };

    /// <summary>Tipos que llevan datos personales: nunca se registran con sus campos.</summary>
    private static readonly HashSet<Type> PersonalTypes =
    [
        typeof(WooCommerceOrder),
        typeof(WooCommerceAddress),
        typeof(NewSapCustomer),
        typeof(SapCustomer),
        typeof(SapAddress),
        typeof(ClaimedJob),
        typeof(SyncJob),
        typeof(JobDetail),
    ];

    /// <summary>Agrega la política a la configuración de Serilog.</summary>
    public static LoggerConfiguration ProtectPersonalData(this LoggerConfiguration logger) =>
        logger.Destructure.With<PersonalDataDestructuringPolicy>().Enrich.With<PersonalDataEnricher>();

    internal static bool IsPersonalProperty(string name) => PersonalProperties.Contains(name);

    internal static bool IsPersonalType(Type type) => PersonalTypes.Contains(type);

    /// <summary>Evita que un objeto del pedido se desarme en sus campos al registrarlo con <c>{@Objeto}</c>.</summary>
    private sealed class PersonalDataDestructuringPolicy : IDestructuringPolicy
    {
        public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory, out LogEventPropertyValue result)
        {
            if (IsPersonalType(value.GetType()))
            {
                result = new ScalarValue(Hidden);
                return true;
            }

            result = null!;
            return false;
        }
    }

    /// <summary>Oculta lo personal de las propiedades de cada evento.</summary>
    private sealed class PersonalDataEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            foreach (var property in logEvent.Properties.ToList())
            {
                var safe = Scrub(property.Key, property.Value);
                if (!ReferenceEquals(safe, property.Value))
                {
                    logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, safe));
                }
            }
        }

        private static LogEventPropertyValue Scrub(string? name, LogEventPropertyValue value)
        {
            if (name is not null && IsPersonalProperty(name))
            {
                return new ScalarValue(Hidden);
            }

            switch (value)
            {
                // Sin la arroba, Serilog convierte el objeto a texto con ToString al armar el evento (el de un record imprime todos sus
                // campos): ya no se ve su tipo, pero sí el encabezado que ToString le pone.
                case ScalarValue { Value: string rendered } when LooksLikePersonalObject(rendered):
                    return new ScalarValue(Hidden);

                case ScalarValue { Value: string text }:
                    var clean = PersonalDataRedactor.Redact(PersonalDataScope.Redact(text));
                    return clean == text ? value : new ScalarValue(clean);

                case ScalarValue { Value: { } other } when IsPersonalType(other.GetType()):
                    return new ScalarValue(Hidden);

                case StructureValue structure:
                    return ScrubStructure(structure);

                case SequenceValue sequence:
                    var elements = sequence.Elements.Select(element => Scrub(null, element)).ToList();
                    return elements.Zip(sequence.Elements).All(pair => ReferenceEquals(pair.First, pair.Second)) ? value : new SequenceValue(elements);

                case DictionaryValue dictionary:
                    var items = dictionary.Elements.Select(item => KeyValuePair.Create(item.Key, Scrub(item.Key.Value as string, item.Value))).ToList();
                    return items.Zip(dictionary.Elements).All(pair => ReferenceEquals(pair.First.Value, pair.Second.Value)) ? value : new DictionaryValue(items);

                default:
                    return value;
            }
        }

        private static bool LooksLikePersonalObject(string text) =>
            PersonalTypes.Any(type => text.StartsWith(type.Name + " {", StringComparison.Ordinal));

        private static StructureValue ScrubStructure(StructureValue structure)
        {
            var properties = structure.Properties.Select(property => new LogEventProperty(property.Name, Scrub(property.Name, property.Value))).ToList();
            return properties.Zip(structure.Properties).All(pair => ReferenceEquals(pair.First.Value, pair.Second.Value))
                ? structure
                : new StructureValue(properties, structure.TypeTag);
        }
    }
}
