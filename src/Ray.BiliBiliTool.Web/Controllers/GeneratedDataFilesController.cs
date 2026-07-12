using Microsoft.AspNetCore.Mvc;
using Ray.BiliBiliTool.Web.Services;

namespace Ray.BiliBiliTool.Web.Controllers;

[ApiController]
[Route("api/generated-files")]
public sealed class GeneratedDataFilesController(IDataMaintenanceService dataMaintenanceService)
    : ControllerBase
{
    [HttpGet("download")]
    public async Task<IActionResult> Download(
        [FromQuery] string id,
        CancellationToken cancellationToken
    )
    {
        var file = await dataMaintenanceService.OpenGeneratedFileAsync(id, cancellationToken);
        return file is null
            ? NotFound()
            : File(file.Content, file.ContentType, file.FileName, enableRangeProcessing: true);
    }
}
