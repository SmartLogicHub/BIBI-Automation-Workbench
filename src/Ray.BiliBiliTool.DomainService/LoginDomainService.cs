using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QRCoder;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.Passport;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;
using Ray.BiliBiliTool.Agent.QingLong;
using Ray.BiliBiliTool.Agent.QingLong.Dtos;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.DomainService.Interfaces;
using Ray.BiliBiliTool.Infrastructure.Cookie;

namespace Ray.BiliBiliTool.DomainService;

/// <summary>
/// 账户
/// </summary>
public class LoginDomainService(
    ILogger<LoginDomainService> logger,
    IPassportApi passportApi,
    IHostEnvironment hostingEnvironment,
    IQingLongApi qingLongApi,
    IHomeApi homeApi,
    IConfiguration configuration,
    IOptions<QingLongOptions> qingLongOptions
) : ILoginDomainService
{
    private static readonly SemaphoreSlim CookieFileGate = new(1, 1);

    public async Task<BiliCookie> LoginByQrCodeAsync(CancellationToken cancellationToken)
    {
        BiliCookie? cookieInfo = null;

        var re = await passportApi.GenerateQrCode();
        if (re.Code != 0)
        {
            throw new Exception($"获取二维码失败：{re.ToJsonStr()}");
        }

        var url = re.Data.Url;
        GenerateQrCode(url);

        var online = GetOnlinePic(url);
        logger.LogInformation(Environment.NewLine + Environment.NewLine);
        logger.LogInformation(
            "如果上方二维码显示异常，或扫描失败，请使用浏览器访问如下链接，查看高清二维码："
        );
        logger.LogInformation(online + Environment.NewLine + Environment.NewLine);

        var waitTimes = 10;
        logger.LogInformation("我数到{num}，动作快点", waitTimes);
        for (int i = 0; i < waitTimes; i++)
        {
            logger.LogInformation("[{num}]等待扫描...", i + 1);

            await Task.Delay(5 * 1000, cancellationToken);

            var check = await passportApi.CheckQrCodeHasScaned(re.Data.Qrcode_key);
            if (!check.IsSuccessStatusCode)
            {
                logger.LogWarning("调用检测接口异常");
                continue;
            }

            var contentStr = await check.Content.ReadAsStringAsync(cancellationToken);
            var content = JsonConvert.DeserializeObject<BiliApiResponse<TokenDto>>(contentStr);
            if (content?.Code != 0)
            {
                logger.LogWarning("调用检测接口异常：{msg}", check.ToJsonStr());
                break;
            }

            if (content.Data.Code == 86038) //已失效
            {
                logger.LogInformation(content.Data.Message);
                break;
            }

            if (content.Data.Code == 0)
            {
                logger.LogInformation("扫描成功！");
                IEnumerable<string> cookies = check
                    .Headers.SingleOrDefault(header => header.Key == "Set-Cookie")
                    .Value;

                var cookieStr = CookieInfo.ConvertSetCkHeadersToCkStr(cookies);

                cookieInfo = CookieStrFactory<BiliCookie>.CreateNew(cookieStr);
                cookieInfo.Check();

                break;
            }

            logger.LogInformation("{msg}", content.Data.Message + Environment.NewLine);
        }

        if (cookieInfo == null)
        {
            throw new Exception("登录超时");
        }

        return cookieInfo;
    }

    public async Task<BiliCookie> SetCookieAsync(
        BiliCookie biliCookie,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var homePage = await homeApi.GetHomePageAsync(biliCookie.ToString());
            if (homePage.IsSuccessStatusCode)
            {
                logger.LogInformation("访问主站成功");
                IEnumerable<string> setCookieHeaders = homePage
                    .Headers.SingleOrDefault(header => header.Key == "Set-Cookie")
                    .Value;
                if (setCookieHeaders != null)
                {
                    biliCookie.MergeCurrentCookieBySetCookieHeaders(setCookieHeaders);
                    logger.LogInformation("SetCookie成功");
                }
                else
                {
                    logger.LogInformation("无需set");
                }

                return biliCookie;
            }
            logger.LogError("访问主站失败：{msg}", homePage.ToJsonStr());
        }
        catch (Exception e)
        {
            //buvid只影响分享和投币，可以吞掉异常
            logger.LogError(e.ToJsonStr());
        }

        return biliCookie;
    }

    public async Task SaveCookieToJsonFileAsync(
        BiliCookie ckInfo,
        CancellationToken cancellationToken
    )
    {
        await CookieFileGate.WaitAsync(cancellationToken);
        try
        {
            var fileInfo = GetCookieFileInfo();
            var path =
                fileInfo.PhysicalPath
                ?? throw new InvalidOperationException("无法确定本地账号会话文件位置");
            logger.LogInformation("目标json地址：{path}", path);

            var root = await ReadCookieStoreAsync(path, cancellationToken);
            if (root["BiliBiliCookies"] is not JArray accounts)
            {
                accounts = [];
                root["BiliBiliCookies"] = accounts;
            }

            ckInfo.CookieItemDictionary.TryGetValue("DedeUserID", out var userId);
            userId ??= ckInfo.CookieStr;
            var existing = accounts
                .Where(token => token.Type == JTokenType.String)
                .FirstOrDefault(token => CookieBelongsToUser(token.Value<string>() ?? "", userId));
            if (existing is null)
            {
                accounts.Add(ckInfo.CookieStr);
                logger.LogInformation("不存在该用户，新增cookie");
            }
            else
            {
                existing.Replace(new JValue(ckInfo.CookieStr));
                logger.LogInformation("已存在该用户，更新cookie");
            }

            await WriteCookieStoreAtomicallyAsync(path, root, cancellationToken);
            logger.LogInformation("账号会话保存成功");
        }
        finally
        {
            CookieFileGate.Release();
        }
    }

    public async Task DeleteCookieFromJsonFileAsync(
        string userId,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(userId))
            return;

        await CookieFileGate.WaitAsync(cancellationToken);
        try
        {
            var fileInfo = GetCookieFileInfo();
            if (!fileInfo.Exists || string.IsNullOrWhiteSpace(fileInfo.PhysicalPath))
                return;

            var root = await ReadCookieStoreAsync(fileInfo.PhysicalPath, cancellationToken);
            if (root["BiliBiliCookies"] is not JArray accounts)
                return;

            var matches = accounts
                .Where(token => token.Type == JTokenType.String)
                .Where(token => CookieBelongsToUser(token.Value<string>() ?? "", userId))
                .ToList();
            foreach (var match in matches)
                match.Remove();

            if (matches.Count > 0)
            {
                await WriteCookieStoreAtomicallyAsync(
                    fileInfo.PhysicalPath,
                    root,
                    cancellationToken
                );
                logger.LogInformation("账号 {UserId} 的本地登录会话已删除", userId);
            }
        }
        finally
        {
            CookieFileGate.Release();
        }
    }

    public async Task<bool> SaveCookieToQinLongAsync(
        BiliCookie ckInfo,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var token = await GetQingLongAuthTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                throw new Exception("获取青龙token失败");
            }

            var qlEnvList = await qingLongApi.GetEnvsAsync("Ray_BiliBiliCookies__", token);
            if (qlEnvList.Code != 200)
            {
                throw new Exception($"查询环境变量失败：{qlEnvList.ToJsonStr()}");
            }

            logger.LogDebug(qlEnvList.Data.ToJsonStr());
            logger.LogDebug(ckInfo.ToString());

            var list = qlEnvList
                .Data.Where(x => x.name.StartsWith("Ray_BiliBiliCookies__"))
                .ToList();
            var oldEnv = list.FirstOrDefault(x => x.value.Contains(ckInfo.UserId));

            if (oldEnv != null)
            {
                logger.LogInformation("用户已存在，更新cookie");
                logger.LogInformation("Key：{key}", oldEnv.name);
                var update = new UpdateQingLongEnv
                {
                    id = oldEnv.id,
                    name = oldEnv.name,
                    value = ckInfo.CookieStr,
                    remarks = string.IsNullOrEmpty(oldEnv.remarks)
                        ? $"bili-{ckInfo.UserId}"
                        : oldEnv.remarks,
                };

                var updateRe = await qingLongApi.UpdateEnvsAsync(update, token);
                logger.LogInformation(updateRe.Code == 200 ? "更新成功！" : updateRe.ToJsonStr());

                return true;
            }

            logger.LogInformation("用户不存在，新增cookie");
            var maxNum = -1;
            if (list.Any())
            {
                maxNum = list.Select(x =>
                    {
                        var num = x.name.Replace("Ray_BiliBiliCookies__", "");
                        var parseSuc = int.TryParse(num, out int envNum);
                        return parseSuc ? envNum : 0;
                    })
                    .Max();
            }

            var name = $"Ray_BiliBiliCookies__{maxNum + 1}";
            logger.LogInformation("Key：{key}", name);

            var add = new AddQingLongEnv
            {
                name = name,
                value = ckInfo.CookieStr,
                remarks = $"bili-{ckInfo.UserId}",
            };
            var addRe = await qingLongApi.AddEnvsAsync([add], token);
            logger.LogInformation(addRe.Code == 200 ? "新增成功！" : addRe.ToJsonStr());
            return true;
        }
        catch
        {
            await PrintIfSaveCookieFailAsync(ckInfo, cancellationToken);
            return false;
        }
    }

    #region private

    private void GenerateQrCode(string str)
    {
        var qrGenerator = new QRCodeGenerator();
        QRCodeData qrCodeData = qrGenerator.CreateQrCode(str, QRCodeGenerator.ECCLevel.L);

        logger.LogInformation("AsciiQRCode：");
        //var qrCode = new AsciiQRCode(qrCodeData);
        //var qrCodeStr = qrCode.GetGraphic(1, drawQuietZones: false);
        //_logger.LogInformation(Environment.NewLine + qrCodeStr);

        //Console.WriteLine("Console：");
        //Print(qrCodeData);
        PrintSmall(qrCodeData);
    }

    private void Print(QRCodeData qrCodeData)
    {
        Console.BackgroundColor = ConsoleColor.White;
        for (int i = 0; i < qrCodeData.ModuleMatrix.Count + 2; i++)
            Console.Write("　"); //中文全角的空格符
        Console.WriteLine();
        for (int j = 0; j < qrCodeData.ModuleMatrix.Count; j++)
        {
            for (int i = 0; i < qrCodeData.ModuleMatrix.Count; i++)
            {
                //char charToPoint = qrCode.Matrix[i, j] ? '█' : '　';
                Console.Write(i == 0 ? "　" : ""); //中文全角的空格符
                Console.BackgroundColor = qrCodeData.ModuleMatrix[i][j]
                    ? ConsoleColor.Black
                    : ConsoleColor.White;
                Console.Write('　'); //中文全角的空格符
                Console.BackgroundColor = ConsoleColor.White;
                Console.Write(i == qrCodeData.ModuleMatrix.Count - 1 ? "　" : ""); //中文全角的空格符
            }
            Console.WriteLine();
        }
        for (int i = 0; i < qrCodeData.ModuleMatrix.Count + 2; i++)
            Console.Write("　"); //中文全角的空格符

        Console.WriteLine();
    }

    private void PrintSmall(QRCodeData qrCodeData)
    {
        //黑黑（" "）
        //白白（"█"）
        //黑白（"▄"）
        //白黑（"▀"）
        var dic = new Dictionary<string, char>()
        {
            { "11", ' ' },
            { "00", '█' },
            { "10", '▄' },
            { "01", '▀' }, //todo:win平台的cmd会显示？,是已知问题，待想办法解决
            //{"01", '^'},//▼▔
        };

        var count = qrCodeData.ModuleMatrix.Count;

        var list = new List<string>();
        for (int rowNum = 0; rowNum < count; rowNum++)
        {
            var rowStr = "";
            for (int colNum = 0; colNum < count; colNum++)
            {
                var num = qrCodeData.ModuleMatrix[colNum][rowNum] ? "1" : "0";
                var numDown = "0";
                if (rowNum + 1 < count)
                    numDown = qrCodeData.ModuleMatrix[colNum][rowNum + 1] ? "1" : "0";

                rowStr += dic[num + numDown];
            }
            list.Add(rowStr);
            rowNum++;
        }

        logger.LogInformation(Environment.NewLine + string.Join(Environment.NewLine, list));
    }

    private string GetOnlinePic(string str)
    {
        var encode = System.Web.HttpUtility.UrlEncode(str);
        return $"https://tool.lu/qrcode/basic.html?text={encode}";
    }

    private static async Task<JObject> ReadCookieStoreAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(path))
            return new JObject();

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return new JObject();

        try
        {
            return JObject.Parse(json);
        }
        catch (JsonReaderException ex)
        {
            throw new InvalidDataException("本地账号会话文件格式无效，请先恢复或删除该文件。", ex);
        }
    }

    private static async Task WriteCookieStoreAtomicallyAsync(
        string path,
        JObject root,
        CancellationToken cancellationToken
    )
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                root.ToString(Formatting.Indented),
                cancellationToken
            );
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private IFileInfo GetCookieFileInfo()
    {
        var path = hostingEnvironment.ContentRootPath;
        var indexOfBin = path.LastIndexOf("bin", StringComparison.OrdinalIgnoreCase);
        if (indexOfBin != -1)
            path = path[..indexOfBin];
        if (string.Equals(configuration["PlatformType"], "Web", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(path, "data");

        Directory.CreateDirectory(path);
        var fileProvider = new PhysicalFileProvider(path);
        return fileProvider.GetFileInfo("cookies.json");
    }

    private static bool CookieBelongsToUser(string cookie, string userId)
    {
        return cookie
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Split('=', 2))
            .Any(parts =>
                parts.Length == 2
                && parts[0].Equals("DedeUserID", StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals(userId, StringComparison.OrdinalIgnoreCase)
            );
    }

    #region qinglong

    private async Task<string> GetQingLongAuthTokenAsync()
    {
        logger.LogWarning("使用OpenAPI鉴权");
        if (
            string.IsNullOrWhiteSpace(qingLongOptions.Value.ClientId)
            || string.IsNullOrWhiteSpace(qingLongOptions.Value.ClientSecret)
        )
        {
            logger.LogWarning("未配置青龙的ClientId和ClientSecret，无法自动获取token");
            logger.LogWarning(
                "教程：{qingDoc}",
                "https://github.com/RayWangQvQ/BiliBiliToolPro/blob/main/qinglong/README.md"
            );
            return "";
        }

        var token = await qingLongApi.GetTokenAsync(
            qingLongOptions.Value.ClientId!,
            qingLongOptions.Value.ClientSecret!
        );

        return $"{token.Data.token_type} {token.Data.token}";
    }

    private Task PrintIfSaveCookieFailAsync(BiliCookie ckInfo, CancellationToken cancellationToken)
    {
        logger.LogError("持久化失败，青龙版本高于2.18，请手动添加环境变量到青龙");
        logger.LogWarning("变量Key：{key}", "Ray_BiliBiliCookies__0");
        logger.LogWarning("变量值：{value}", ckInfo.CookieStr);
        logger.LogWarning(
            "如果Key已存在，请自行+1，如Ray_BiliBiliCookies__1，Ray_BiliBiliCookies__2..."
        );
        return Task.CompletedTask;
    }

    #endregion

    #endregion
}
