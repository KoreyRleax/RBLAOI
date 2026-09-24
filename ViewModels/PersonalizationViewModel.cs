using RBLAOI.Core;
using RBLAOI.Core.Managers;
using RBLAOI.Core.Utility;
using RBLAOI.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Media;

namespace RBLAOI.ViewModels
{
    public class PersonalizationViewModel : NotificationObject
    {
        private SysParam _sysParam;

        public PersonalizationViewModel()
        {
            _sysParam = SysParam.Instance;
            LoadData();
            LoadSystemFonts();
            SaveOriginalValues();
        }

        // ========== 绑定属性 ==========

        [SysParam] public int TextOffsetX { get; set; } = 10;
        [SysParam] public int TextOffsetY { get; set; } = -7;
        [SysParam] public string TextDirection { get; set; } = "Horizontal";
        [SysParam] public int OverlayFontSize { get; set; } = 10;
        [SysParam] public int MarkerFontSize { get; set; } = 22;
        [SysParam] public string OverlayFontName { get; set; } = "Consolas";
        [SysParam] public string PadColor { get; set; } = "cyan";
        [SysParam] public string PinColor { get; set; } = "yellow";
        [SysParam] public string IdealPinColor { get; set; } = "gold";
        [SysParam] public string TextBgColor { get; set; } = "";

        [SysParam] public string EmptyPinColor { get; set; } = "orange";
        [SysParam] public double IdealPinDotRadius { get; set; } = 3;

        // ========== 标注框显示开关 ==========
        [SysParam] public bool ShowPadBox { get; set; } = true;
        [SysParam] public bool ShowPinBox { get; set; } = true;
        [SysParam] public bool ShowIdealPinBox { get; set; } = true;
        [SysParam] public bool ShowEmptyPinBox { get; set; } = true;

        // ========== 检测位选择设置 ==========
        [SysParam] public string SelectionBoxColor { get; set; } = "red";
        [SysParam] public int SelectionBoxSize { get; set; } = 1;
        [SysParam] public int MarkerTextMargin { get; set; } = 6;        // 针型文字边距（像素，字符间距）
        [SysParam] public string MarkerTextBgColor { get; set; } = "";   // 针型文字背景色（空=无背景）

        // ========== 字体列表 ==========
        public ObservableCollection<string> AvailableFonts { get; set; } = new ObservableCollection<string>();

        // ========== Halcon 有效颜色列表 ==========
        public ObservableCollection<ColorItem> AvailableColors { get; set; } = new ObservableCollection<ColorItem>
        {
            // 基础色
            new ColorItem { Display = "白 (White)", Value = "white" },
            new ColorItem { Display = "黑 (Black)", Value = "black" },
            new ColorItem { Display = "红 (Red)", Value = "red" },
            new ColorItem { Display = "绿 (Green)", Value = "green" },
            new ColorItem { Display = "蓝 (Blue)", Value = "blue" },
            new ColorItem { Display = "青 (Cyan)", Value = "cyan" },
            new ColorItem { Display = "品红 (Magenta)", Value = "magenta" },
            new ColorItem { Display = "黄 (Yellow)", Value = "yellow" },
            new ColorItem { Display = "橙 (Orange)", Value = "orange" },
            new ColorItem { Display = "粉红 (Pink)", Value = "pink" },
            new ColorItem { Display = "金 (Gold)", Value = "gold" },
            new ColorItem { Display = "海军蓝 (Navy)", Value = "navy" },
            // 珊瑚色系
            new ColorItem { Display = "珊瑚 (Coral)", Value = "coral" },
            new ColorItem { Display = "鲑鱼 (Salmon)", Value = "salmon" },
            // 植物绿色系
            new ColorItem { Display = "春绿 (Spring green)", Value = "spring green" },
            new ColorItem { Display = "森林绿 (Forest green)", Value = "forest green" },
            new ColorItem { Display = "深橄榄绿 (Dark olive green)", Value = "dark olive green" },
            new ColorItem { Display = "海绿 (Sea green)", Value = "sea green" },
            new ColorItem { Display = "苍绿 (Pale green)", Value = "pale green" },
            new ColorItem { Display = "酸橙绿 (Lime green)", Value = "lime green" },
            new ColorItem { Display = "黄绿 (Yellow green)", Value = "yellow green" },
            new ColorItem { Display = "绿黄 (Green yellow)", Value = "green yellow" },
            new ColorItem { Display = "深绿 (Dark green)", Value = "dark green" },
            // 蓝色系
            new ColorItem { Display = "军校蓝 (Cadet blue)", Value = "cadet blue" },
            new ColorItem { Display = "矢车菊蓝 (Cornflower blue)", Value = "cornflower blue" },
            new ColorItem { Display = "深石板蓝 (Dark slate blue)", Value = "dark slate blue" },
            new ColorItem { Display = "浅蓝 (Light blue)", Value = "light blue" },
            new ColorItem { Display = "浅钢蓝 (Light steel blue)", Value = "light steel blue" },
            new ColorItem { Display = "中蓝 (Medium blue)", Value = "medium blue" },
            new ColorItem { Display = "午夜蓝 (Midnight blue)", Value = "midnight blue" },
            new ColorItem { Display = "天蓝 (Sky blue)", Value = "sky blue" },
            new ColorItem { Display = "钢蓝 (Steel blue)", Value = "steel blue" },
            new ColorItem { Display = "蓝绿 (Turquoise)", Value = "turquoise" },
            new ColorItem { Display = "暗蓝绿 (Dark turquoise)", Value = "dark turquoise" },
            new ColorItem { Display = "中蓝绿 (Medium turquoise)", Value = "medium turquoise" },
            new ColorItem { Display = "中海绿 (Medium aquamarine)", Value = "medium aquamarine" },
            new ColorItem { Display = "海绿 (Aquamarine)", Value = "aquamarine" },
            // 紫色/粉红系
            new ColorItem { Display = "紫罗兰红 (Violet red)", Value = "violet red" },
            new ColorItem { Display = "紫罗兰 (Violet)", Value = "violet" },
            new ColorItem { Display = "蓝紫罗兰 (Blue violet)", Value = "blue violet" },
            new ColorItem { Display = "洋红 (Magenta)", Value = "magenta" },
            new ColorItem { Display = "兰花 (Orchid)", Value = "orchid" },
            new ColorItem { Display = "暗兰花 (Dark orchid)", Value = "dark orchid" },
            new ColorItem { Display = "中兰花 (Medium orchid)", Value = "medium orchid" },
            new ColorItem { Display = "李色 (Plum)", Value = "plum" },
            new ColorItem { Display = "蓟色 (Thistle)", Value = "thistle" },
            new ColorItem { Display = "中紫罗兰红 (Medium violet red)", Value = "medium violet red" },
            // 大地色系
            new ColorItem { Display = "赭色 (Sienna)", Value = "sienna" },
            new ColorItem { Display = "棕褐 (Tan)", Value = "tan" },
            new ColorItem { Display = "小麦 (Wheat)", Value = "wheat" },
            new ColorItem { Display = "卡其 (Khaki)", Value = "khaki" },
            new ColorItem { Display = "金棍 (Goldenrod)", Value = "goldenrod" },
            new ColorItem { Display = "中金棍 (Medium goldenrod)", Value = "medium goldenrod" },
            // 灰色系
            new ColorItem { Display = "暗灰 (Dim gray)", Value = "dim gray" },
            new ColorItem { Display = "灰 (Gray)", Value = "gray" },
            new ColorItem { Display = "浅灰 (Light gray)", Value = "light gray" },
        };

        // ========== 文字背景色选项（无背景 + 常用色） ==========
        public ObservableCollection<ColorItem> BgColorOptions { get; set; } = new ObservableCollection<ColorItem>
        {
            new ColorItem { Display = "无背景", Value = "" },
            new ColorItem { Display = "白 (White)", Value = "white" },
            new ColorItem { Display = "黑 (Black)", Value = "black" },
            new ColorItem { Display = "红 (Red)", Value = "red" },
            new ColorItem { Display = "绿 (Green)", Value = "green" },
            new ColorItem { Display = "蓝 (Blue)", Value = "blue" },
            new ColorItem { Display = "青 (Cyan)", Value = "cyan" },
            new ColorItem { Display = "黄 (Yellow)", Value = "yellow" },
            new ColorItem { Display = "灰 (Gray)", Value = "gray" },
            new ColorItem { Display = "橙 (Orange)", Value = "orange" },
            new ColorItem { Display = "粉红 (Pink)", Value = "pink" },
            new ColorItem { Display = "紫罗兰 (Violet)", Value = "violet" },
        };

        // ========== 文字显示方向选项 ==========
        public ObservableCollection<string> TextDirectionOptions { get; set; } = new ObservableCollection<string>
        {
            "Horizontal",
            "Vertical",
        };

        // ========== 原始值快照（变更检测用） ==========
        private Dictionary<string, object> _originalValues;

        private void SaveOriginalValues()
        {
            _originalValues = new Dictionary<string, object>();
            foreach (var prop in GetType().GetProperties())
            {
                if (prop.GetCustomAttribute<SysParamAttribute>() != null && prop.CanRead)
                    _originalValues[prop.Name] = prop.GetValue(this);
            }
        }

        public bool HasChanges()
        {
            foreach (var prop in GetType().GetProperties())
            {
                if (prop.GetCustomAttribute<SysParamAttribute>() == null) continue;
                if (!prop.CanRead) continue;
                var current = prop.GetValue(this);
                _originalValues.TryGetValue(prop.Name, out object original);
                if (!Equals(current, original)) return true;
            }
            return false;
        }

        public void Revert()
        {
            foreach (var prop in GetType().GetProperties())
            {
                if (prop.GetCustomAttribute<SysParamAttribute>() == null) continue;
                if (!prop.CanWrite) continue;
                if (_originalValues.TryGetValue(prop.Name, out object original))
                    prop.SetValue(this, original);
            }
        }

        // ========== 数据加载/保存 ==========
        private void LoadData()
        {
            var data = _sysParam.Data;
            if (data == null) return;

            foreach (var prop in GetType().GetProperties())
            {
                if (prop.GetCustomAttribute<SysParamAttribute>() == null) continue;
                if (!prop.CanWrite) continue;

                var srcProp = typeof(SysParamData).GetProperty(prop.Name);
                if (srcProp != null && srcProp.CanRead)
                {
                    var value = srcProp.GetValue(data);
                    prop.SetValue(this, value);
                }
            }
        }

        public void Save()
        {
            var data = _sysParam.Data;
            if (data == null) return;

            foreach (var prop in GetType().GetProperties())
            {
                if (prop.GetCustomAttribute<SysParamAttribute>() == null) continue;
                if (!prop.CanRead) continue;

                var dstProp = typeof(SysParamData).GetProperty(prop.Name);
                if (dstProp != null && dstProp.CanWrite)
                {
                    var value = prop.GetValue(this);
                    dstProp.SetValue(data, value);
                }
            }

            _sysParam.Save();
            SaveOriginalValues();
        }

        private void LoadSystemFonts()
        {
            AvailableFonts.Clear();
            foreach (var font in Fonts.SystemFontFamilies)
            {
                var name = font.Source;
                if (name.Contains("Consolas") || name.Contains("Courier") ||
                    name.Contains("Lucida") || name.Contains("YaHei") ||
                    name.Contains("Arial") || name.Contains("Verdana") ||
                    name.Contains("Segoe") || name.Contains("Microsoft"))
                {
                    AvailableFonts.Add(name);
                }
            }
            if (AvailableFonts.Count == 0)
            {
                AvailableFonts.Add("Consolas");
                AvailableFonts.Add("Arial");
                AvailableFonts.Add("Microsoft YaHei");
            }
        }

        public void Dispose()
        {
            _originalValues?.Clear();
            _originalValues = null;
        }
    }

    public class ColorItem
    {
        public string Display { get; set; }
        public string Value { get; set; }
    }
}
