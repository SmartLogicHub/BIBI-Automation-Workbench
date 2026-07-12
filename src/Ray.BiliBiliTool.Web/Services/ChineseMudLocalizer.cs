using Microsoft.Extensions.Localization;
using MudBlazor;

namespace Ray.BiliBiliTool.Web.Services;

public sealed class ChineseMudLocalizer : MudLocalizer
{
    private static readonly IReadOnlyDictionary<string, string> Translations = new Dictionary<
        string,
        string
    >
    {
        ["Converter_ConversionError"] = "转换错误：{0}",
        ["Converter_ConversionFailed"] = "从 {0} 转换为 {1} 失败：{2}",
        ["Converter_ConversionNotImplemented"] = "暂不支持转换为 {0}",
        ["Converter_InvalidBoolean"] = "不是有效的布尔值",
        ["Converter_InvalidDateTime"] = "不是有效的日期时间",
        ["Converter_InvalidGUID"] = "不是有效的 GUID",
        ["Converter_InvalidNumber"] = "不是有效的数字",
        ["Converter_InvalidTimeSpan"] = "不是有效的时间跨度",
        ["Converter_InvalidType"] = "不是有效的 {0}",
        ["Converter_NotValueOf"] = "不是 {0} 的有效值",
        ["Converter_UnableToConvert"] = "无法从类型 {1} 转换为 {0}",

        ["HeatMap_Less"] = "较少",
        ["HeatMap_More"] = "较多",

        ["MudAlert_Close"] = "关闭提示",
        ["MudBaseDatePicker_NextMonth"] = "下个月 {0}",
        ["MudBaseDatePicker_NextYear"] = "下一年 {0}",
        ["MudBaseDatePicker_PrevMonth"] = "上个月 {0}",
        ["MudBaseDatePicker_PrevYear"] = "上一年 {0}",
        ["MudCarousel_Index"] = "第 {0} 项",
        ["MudCarousel_Next"] = "下一项",
        ["MudCarousel_Previous"] = "上一项",
        ["MudChip_Close"] = "关闭标签",
        ["MudColorPicker_AlphaSlider"] = "透明度滑块",
        ["MudColorPicker_Close"] = "关闭颜色选择器",
        ["MudColorPicker_ColorDot"] = "选择颜色点",
        ["MudColorPicker_GridView"] = "切换到网格视图",
        ["MudColorPicker_HueSlider"] = "色相滑块",
        ["MudColorPicker_ModeSwitch"] = "切换模式",
        ["MudColorPicker_PaletteColor"] = "选择调色板颜色",
        ["MudColorPicker_PaletteView"] = "切换到调色板视图",
        ["MudColorPicker_SpectrumView"] = "切换到色谱视图",
        ["MudColorPicker_ToggleCurrentColor"] = "切换当前颜色",

        ["MudDataGrid_AddFilter"] = "添加筛选",
        ["MudDataGrid_Apply"] = "应用",
        ["MudDataGrid_Cancel"] = "取消",
        ["MudDataGrid_Clear"] = "清空",
        ["MudDataGrid_ClearFilter"] = "清除筛选",
        ["MudDataGrid_CollapseAllGroups"] = "折叠全部分组",
        ["MudDataGrid_Column"] = "列",
        ["MudDataGrid_Columns"] = "列",
        ["MudDataGrid_Contains"] = "包含",
        ["MudDataGrid_EndsWith"] = "结尾为",
        ["MudDataGrid_Equals"] = "等于",
        ["MudDataGrid_EqualSign"] = "=",
        ["MudDataGrid_ExpandAllGroups"] = "展开全部分组",
        ["MudDataGrid_False"] = "否",
        ["MudDataGrid_Filter"] = "筛选",
        ["MudDataGrid_FilterValue"] = "筛选值",
        ["MudDataGrid_GreaterThanOrEqualSign"] = ">=",
        ["MudDataGrid_GreaterThanSign"] = ">",
        ["MudDataGrid_Group"] = "分组",
        ["MudDataGrid_Hide"] = "隐藏",
        ["MudDataGrid_HideAll"] = "全部隐藏",
        ["MudDataGrid_Is"] = "是",
        ["MudDataGrid_IsAfter"] = "晚于",
        ["MudDataGrid_IsBefore"] = "早于",
        ["MudDataGrid_IsEmpty"] = "为空",
        ["MudDataGrid_IsNot"] = "不是",
        ["MudDataGrid_IsNotEmpty"] = "不为空",
        ["MudDataGrid_IsOnOrAfter"] = "等于或晚于",
        ["MudDataGrid_IsOnOrBefore"] = "等于或早于",
        ["MudDataGrid_LessThanOrEqualSign"] = "<=",
        ["MudDataGrid_LessThanSign"] = "<",
        ["MudDataGrid_Loading"] = "正在加载...",
        ["MudDataGrid_MoveDown"] = "下移",
        ["MudDataGrid_MoveUp"] = "上移",
        ["MudDataGrid_NotContains"] = "不包含",
        ["MudDataGrid_NotEquals"] = "不等于",
        ["MudDataGrid_NotEqualSign"] = "!=",
        ["MudDataGrid_OpenFilters"] = "打开筛选",
        ["MudDataGrid_Operator"] = "条件",
        ["MudDataGrid_RefreshData"] = "刷新数据",
        ["MudDataGrid_RemoveFilter"] = "移除筛选",
        ["MudDataGrid_Save"] = "保存",
        ["MudDataGrid_ShowAll"] = "显示全部",
        ["MudDataGrid_ShowColumnOptions"] = "显示列设置",
        ["MudDataGrid_Sort"] = "排序",
        ["MudDataGrid_StartsWith"] = "开头为",
        ["MudDataGrid_ToggleGroupExpansion"] = "切换分组展开",
        ["MudDataGrid_True"] = "是",
        ["MudDataGrid_Ungroup"] = "取消分组",
        ["MudDataGrid_Unsort"] = "取消排序",
        ["MudDataGrid_Value"] = "值",

        ["MudDataGridPager_AllItems"] = "全部",
        ["MudDataGridPager_FirstPage"] = "第一页",
        ["MudDataGridPager_InfoFormat"] = "{0}-{1} / 共 {2}",
        ["MudDataGridPager_LastPage"] = "最后一页",
        ["MudDataGridPager_NextPage"] = "下一页",
        ["MudDataGridPager_PreviousPage"] = "上一页",
        ["MudDataGridPager_RowsPerPage"] = "每页行数：",

        ["MudDialog_Close"] = "关闭弹窗",
        ["MudInput_Clear"] = "清空",
        ["MudInput_Decrement"] = "减少",
        ["MudInput_Increment"] = "增加",
        ["MudNavGroup_ToggleExpand"] = "展开或收起 {0}",
        ["MudPageContentNavigation_NavMenu"] = "目录",
        ["MudPagination_CurrentPage"] = "当前第 {0} 页",
        ["MudPagination_FirstPage"] = "第一页",
        ["MudPagination_LastPage"] = "最后一页",
        ["MudPagination_NextPage"] = "下一页",
        ["MudPagination_PageIndex"] = "第 {0} 页",
        ["MudPagination_PreviousPage"] = "上一页",
        ["MudRatingItem_Label"] = "{0} 评分",
        ["MudSnackbar_Close"] = "关闭消息",
        ["MudStepper_Complete"] = "完成",
        ["MudStepper_Next"] = "下一步",
        ["MudStepper_Previous"] = "上一步",
        ["MudStepper_Reset"] = "重置",
        ["MudStepper_Skip"] = "跳过",
        ["MudTablePager_FirstPage"] = "第一页",
        ["MudTablePager_LastPage"] = "最后一页",
        ["MudTablePager_NextPage"] = "下一页",
        ["MudTablePager_PreviousPage"] = "上一页",
    };

    public override LocalizedString this[string key] => GetLocalizedString(key);

    public override LocalizedString this[string key, params object[] arguments]
    {
        get
        {
            var value = GetLocalizedString(key);
            return value.ResourceNotFound
                ? value
                : new LocalizedString(key, string.Format(value.Value, arguments));
        }
    }

    private static LocalizedString GetLocalizedString(string key)
    {
        return Translations.TryGetValue(key, out var value)
            ? new LocalizedString(key, value)
            : new LocalizedString(key, key, true);
    }
}
