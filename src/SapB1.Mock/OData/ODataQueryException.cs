namespace SapB1.Mock.OData;

/// <summary>Consulta OData mal formada o que usa propiedades inexistentes.</summary>
internal sealed class ODataQueryException(string message) : Exception(message);
