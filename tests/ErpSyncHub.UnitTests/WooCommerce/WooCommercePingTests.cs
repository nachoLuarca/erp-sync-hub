using System.Text;
using ErpSyncHub.Application.WooCommerce;

namespace ErpSyncHub.UnitTests.WooCommerce;

public class WooCommercePingTests
{
    [Theory]
    [InlineData("webhook_id=123")]
    [InlineData("webhook_id=7")]
    [InlineData("webhook_id=123\n")]
    [InlineData("  webhook_id=123  ")]
    [InlineData("webhook_id=999999999")]
    public void Reconoce_el_ping_de_creacion_de_un_webhook(string body)
    {
        WooCommercePing.IsPing(Encoding.UTF8.GetBytes(body)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("webhook_id=")]
    [InlineData("webhook_id=abc")]
    [InlineData("webhook_id=12x")]
    [InlineData("xwebhook_id=1")]
    [InlineData("webhook_id=1&otro=2")]
    [InlineData("""{"id":5501,"status":"processing"}""")]
    [InlineData("""{"webhook_id":"123"}""")]
    public void No_confunde_otros_cuerpos_con_un_ping(string body)
    {
        WooCommercePing.IsPing(Encoding.UTF8.GetBytes(body)).ShouldBeFalse();
    }

    [Fact]
    public void Un_cuerpo_sin_bytes_no_es_un_ping()
    {
        WooCommercePing.IsPing([]).ShouldBeFalse();
    }
}
