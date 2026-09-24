// TemplateModel.cs
using HalconDotNet;
using RBLAOI.Core.Utility;
using System;

public class TemplateModel : NotificationObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    private string _name;
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    private string _imagePath;
    public string ImagePath
    {
        get => _imagePath;
        set => SetProperty(ref _imagePath, value);
    }

    public double RoiX { get; set; }
    public double RoiY { get; set; }
    public double RoiWidth { get; set; }
    public double RoiHeight { get; set; }

    // 可选：Halcon 模板句柄（用于匹配）
    public HTuple ModelID { get; set; }
}