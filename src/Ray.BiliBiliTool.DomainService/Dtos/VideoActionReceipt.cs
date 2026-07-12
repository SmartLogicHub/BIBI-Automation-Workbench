namespace Ray.BiliBiliTool.DomainService.Dtos;

public sealed class VideoActionReceipt
{
    public bool Confirmed { get; set; }

    public int? PlatformCode { get; set; }

    public string PlatformMessage { get; set; } = "";

    public int Quantity { get; set; } = 1;

    public string Unit { get; set; } = "次";

    public static VideoActionReceipt PlatformConfirmed(
        int quantity = 1,
        string unit = "次",
        string message = "平台已确认"
    ) =>
        new()
        {
            Confirmed = true,
            PlatformCode = 0,
            PlatformMessage = message,
            Quantity = quantity,
            Unit = unit,
        };
}
