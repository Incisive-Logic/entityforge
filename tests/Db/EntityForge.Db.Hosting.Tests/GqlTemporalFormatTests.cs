using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("format(date('2020-02-29'))")]
    [DataRow("format(localtime('12'))")]
    [DataRow("format(time('12Z'))")]
    [DataRow("format(localdatetime('2020-01-01T12'))")]
    [DataRow("format(datetime('2020-01-01T12Z'))")]
    [DataRow("format(date('2020-02-29'),'EEEE MMMM d, uuuu G QQQ')")]
    [DataRow("format(date('2020-02-29'),'Y w W F e c')")]
    [DataRow("format(date('2019-12-29'),'YYYY ww')")]
    [DataRow("format(date('-0001-01-01'),'G uuuu yyyy yy')")]
    [DataRow("format(date('+10000-01-01'),'uuuu')")]
    [DataRow("format(date('2020-02-29'),'pppYY')")]
    [DataRow("format(localtime('12:30:45.123456789'),'HH:mm:ss.SSSSSSSSS')")]
    [DataRow("format(localtime('00'),'h K k H a')")]
    [DataRow("format(localtime('12:30:45.123456789'),'A N n')")]
    [DataRow("format(time('12:30+01:02:03'),'X XX XXX XXXX XXXXX O OOOO Z ZZZZZ')")]
    [DataRow("format(time('12Z'),'X x xx xxx O Z ZZZZZ')")]
    [DataRow("format(datetime('2020-01-01T12[Europe/London]'),'VV')")]
    [DataRow("format(date('2020-02-29'),\"uuuu-MM-dd['T'HH:mm[:ss]]\")")]
    [DataRow("format(date('2020-02-29'),\"'it''s' EEEE\")")]
    [DataRow("format(date('2020-02-29'),'[HH]uuuu')")]
    [DataRow("format(duration('P1Y2M3DT4H5M6.123456789S'))")]
    [DataRow("format(duration('P1Y2M3DT4H5M6.123456789S'),'y M d H m s SSS')")]
    [DataRow("format(duration('P1Y2M3DT4H5M6.123456789S'),'M d m s A N n SSS')")]
    [DataRow("format(duration('P1Y4M'),'q M')")]
    [DataRow("format(duration('P25D'),'w d')")]
    [DataRow("format(duration('-PT1.5S'),'s n SSS A N')")]
    [DataRow("format(null)")]
    [DataRow("format(date('2020'),null)")]
    [DataRow("format(1)")]
    [DataRow("format(date('2020'),1)")]
    [DataRow("format(date('2020'),'HH')")]
    [DataRow("format(time('12Z'),'VV')")]
    [DataRow("format(date('2020'),'MMMMMM')")]
    [DataRow("format(date('2020'),'[unknown]')")]
    [DataRow("format(date('2020'),'pYYYY')")]
    [DataRow("format(date('2020'),']')")]
    public async Task Gql_rejects_vendor_temporal_format_function(string expression)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "RETURN " + expression + " AS answer"
        });
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, body);
    }
}
