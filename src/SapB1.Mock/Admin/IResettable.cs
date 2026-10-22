namespace SapB1.Mock.Admin;

/// <summary>Estado del mock que <c>POST /_mock/reset</c> restaura a sus datos semilla.</summary>
internal interface IResettable
{
    void Reset();
}
