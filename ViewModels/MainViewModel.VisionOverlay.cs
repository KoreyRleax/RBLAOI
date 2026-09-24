using GlobalCameraModuleCs;
using HalconDotNet;
using Microsoft.Win32;
using Newtonsoft.Json;
using RBLAOI.Core;
using RBLAOI.Core.Controls;
using RBLAOI.Core.Device;
using RBLAOI.Core.LightSource;
using RBLAOI.Core.Managers;
using RBLAOI.Core.Motion;
using RBLAOI.Core.Utility;
using RBLAOI.Core.Vision;
using RBLAOI.Models;
using RBLAOI.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VM.Core;
using VM.PlatformSDKCS;

namespace RBLAOI.ViewModels
{
    public partial class MainViewModel : NotificationObject
    {
        #region =========================== VM 检测结果叠加层绘制 ====================

        /// <summary>异步刷新叠加层显示（2026-08-21，显示不阻塞检测流程）：
        /// 发起 UI 线程渲染后立即返回，检测流程不等渲染完成直接推进下一检测位；
        /// 渲染前拍摄 _vmOverlays 快照——渲染排队期间即使下一检测位已修改 _vmOverlays，
        /// 本帧仍按发起时刻的快照绘制，不会画出后续检测位的内容。
        /// 注意：拼接图克隆（GetCurrentStitchedImage）与 Dispose 均在 UI 线程渲染任务内完成，不泄漏。</summary>
        private void RefreshOverlayDisplayAsync()
        {
            var snapshot = _vmOverlays.ToList();   // 浅拷贝快照（元素对象渲染时只读，主线程顺序执行无数据竞争）
            if (snapshot.Count == 0) return;       // 与调用点原条件一致：无叠加层不刷新，保持原画面
            try
            {
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var hww = _vision.HalconWindow;
                        if (hww == null) return;
                        var si = _imageManager.GetCurrentStitchedImage();
                        if (si == null || !si.IsInitialized()) return;
                        HOperatorSet.ClearWindow(hww);
                        HOperatorSet.DispObj(si, hww);
                        if (snapshot.Count > 0) { DrawVmRects(snapshot, hww); DrawVmText(snapshot, hww); }
                        si.Dispose();
                        _vision.RefreshDisplay();
                    }
                    catch { }
                });
            }
            catch { }
        }

        /// <summary>绘制 VM 焊盘（蓝色）/ 针尖（红色）/ 理想针（金色）矩形框（默认使用当前 _vmOverlays）</summary>
        private void DrawVmRects(HWindow hWindow) => DrawVmRects(_vmOverlays, hWindow);

        /// <summary>绘制 VM 焊盘（蓝色）/ 针尖（红色）/ 理想针（金色）矩形框（指定叠加层快照，异步显示用，2026-08-21）</summary>
        private void DrawVmRects(List<VmOverlayItem> overlays, HWindow hWindow)
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null || overlays.Count == 0 || !_isOverlayVisible) return;

            var data = _sysParam.Data;
            var tile = FullImageManager.CalculateTileDimensions(project.FovWidth, project.FovHeight, project.Overlap);
            int cropW = tile.cropW;
            int cropH = tile.cropH;
            int gridCols = _sysParam.ImageDisplayCols;
            double pixelsPerMm = 10.0;
            string idealPinColor = data.IdealPinColor;
            if (string.IsNullOrEmpty(idealPinColor)) idealPinColor = "gold";

            HOperatorSet.SetLineWidth(hWindow, 2);
            HOperatorSet.SetDraw(hWindow, "margin");

            var byGrid = overlays.GroupBy(o => o.GridIndex);
            foreach (var group in byGrid)
            {
                int col = group.Key % gridCols;
                int row = group.Key / gridCols;
                double tileCenterX = col * cropW + cropW / 2.0;
                double tileCenterY = row * cropH + cropH / 2.0;

                double detectX = 0, detectY = 0;
                var grabPos = GrabPositions?.FirstOrDefault(g => g.Index == group.Key);
                if (grabPos != null)
                {
                    detectX = grabPos.X;
                    detectY = grabPos.Y;
                }

                foreach (var item in group)
                {
                    if (_overlayFilter == OverlayResultFilter.OK && !item.IsOK) continue;
                    if (_overlayFilter == OverlayResultFilter.NG && item.IsOK) continue;

                    double padPx = tileCenterX + (item.PadMmX - detectX) * pixelsPerMm;
                    double padPy = tileCenterY - (item.PadMmY - detectY) * pixelsPerMm;
                    double idealPx = tileCenterX + (item.IdealPinMmX - detectX) * pixelsPerMm;
                    double idealPy = tileCenterY - (item.IdealPinMmY - detectY) * pixelsPerMm;

                    double padAngRad = -item.PadAng * Math.PI / 180.0;
                    double padHalfW = (item.PadW > 0 ? item.PadW / 2.0 : 0.5) * pixelsPerMm;
                    double padHalfH = (item.PadH > 0 ? item.PadH / 2.0 : 0.5) * pixelsPerMm;

                    if (item.IsEmptyPin)
                    {
                        // 空针：显示焊盘框 + 理想针框（空针专属颜色），不显示实际针框
                        string emptyColor = data.EmptyPinColor;
                        if (string.IsNullOrEmpty(emptyColor)) emptyColor = "orange";

                        if (data.ShowEmptyPinBox)
                        {
                            // 空针框独立绘制，不依赖 ShowPadBox/ShowIdealPinBox 开关
                            HOperatorSet.SetColor(hWindow, emptyColor);
                            HObject xld_ep = null;
                            HOperatorSet.GenRectangle2ContourXld(out xld_ep, padPy, padPx, padAngRad, padHalfW, padHalfH);
                            HOperatorSet.DispObj(xld_ep, hWindow);
                            xld_ep.Dispose();
                            HOperatorSet.SetColor(hWindow, emptyColor);
                            HOperatorSet.SetDraw(hWindow, "fill");
                            HOperatorSet.DispCircle(hWindow, idealPy, idealPx, _sysParam.Data.IdealPinDotRadius);
                            HOperatorSet.SetDraw(hWindow, "margin");
                        }
                        continue;
                    }

                    if (item.IsCrookedPin)
                    {
                        // 歪针：显示焊盘框 + 理想针框（正常颜色），无针尖框
                        if (data.ShowPadBox)
                        {
                            HOperatorSet.SetColor(hWindow, "blue");
                            HObject xld_pad = null;
                            HOperatorSet.GenRectangle2ContourXld(out xld_pad, padPy, padPx, padAngRad, padHalfW, padHalfH);
                            HOperatorSet.DispObj(xld_pad, hWindow);
                            xld_pad.Dispose();
                        }
                        if (data.ShowIdealPinBox)
                        {
                            HOperatorSet.SetColor(hWindow, idealPinColor);
                            HOperatorSet.SetDraw(hWindow, "fill");
                            HOperatorSet.DispCircle(hWindow, idealPy, idealPx, _sysParam.Data.IdealPinDotRadius);
                            HOperatorSet.SetDraw(hWindow, "margin");
                        }
                        // 歪针不画针尖框
                        continue;
                    }

                    double pinPx = tileCenterX + (item.PinMmX - detectX) * pixelsPerMm;
                    double pinPy = tileCenterY - (item.PinMmY - detectY) * pixelsPerMm;
                    double pinAngRad = -item.PinAng * Math.PI / 180.0;
                    double pinHalfW = (item.PinW > 0 ? item.PinW / 2.0 : 0.3) * pixelsPerMm;
                    double pinHalfH = (item.PinH > 0 ? item.PinH / 2.0 : 0.3) * pixelsPerMm;

                    if (data.ShowPadBox)
                    {
                        HOperatorSet.SetColor(hWindow, "blue");
                        HObject xld_pad = null;
                        HOperatorSet.GenRectangle2ContourXld(out xld_pad, padPy, padPx, padAngRad, padHalfW, padHalfH);
                        HOperatorSet.DispObj(xld_pad, hWindow);
                        xld_pad.Dispose();
                    }
                    if (data.ShowPinBox)
                    {
                        HOperatorSet.SetColor(hWindow, "red");
                        HObject xld_pin = null;
                        HOperatorSet.GenRectangle2ContourXld(out xld_pin, pinPy, pinPx, pinAngRad, pinHalfW, pinHalfH);
                        HOperatorSet.DispObj(xld_pin, hWindow);
                        xld_pin.Dispose();
                    }
                    if (data.ShowIdealPinBox)
                    {
                        HOperatorSet.SetColor(hWindow, idealPinColor);
                        HOperatorSet.SetDraw(hWindow, "fill");
                        HOperatorSet.DispCircle(hWindow, idealPy, idealPx, _sysParam.Data.IdealPinDotRadius);
                        HOperatorSet.SetDraw(hWindow, "margin");
                    }
                }
            }
        }

        /// <summary>绘制 VM 检测文字（OK绿/NG红），带 "VM" 前缀（默认使用当前 _vmOverlays）</summary>
        private void DrawVmText(HWindow hWindow) => DrawVmText(_vmOverlays, hWindow);

        /// <summary>绘制 VM 检测文字（OK绿/NG红），带 "VM" 前缀（指定叠加层快照，异步显示用，2026-08-21）</summary>
        private void DrawVmText(List<VmOverlayItem> overlays, HWindow hWindow)
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null || overlays.Count == 0 || !_isOverlayVisible) return;

            var tile = FullImageManager.CalculateTileDimensions(project.FovWidth, project.FovHeight, project.Overlap);
            int cropW = tile.cropW;
            int cropH = tile.cropH;
            int gridCols = _sysParam.ImageDisplayCols;
            double pixelsPerMm = 10.0;

            int baseFontSize = _sysParam.Data.OverlayFontSize;
            if (baseFontSize < 6) baseFontSize = 10;
            string fontName = _sysParam.Data.OverlayFontName;
            if (string.IsNullOrEmpty(fontName)) fontName = "Consolas";
            int offsetX = _sysParam.Data.TextOffsetX;
            int offsetY = _sysParam.Data.TextOffsetY;
            string textDir2 = _sysParam.Data.TextDirection;

            int fontSize = baseFontSize;
            try
            {
                HOperatorSet.GetPart(hWindow, out HTuple partR1, out HTuple partC1,
                                     out HTuple partR2, out HTuple partC2);
                HOperatorSet.GetWindowExtents(hWindow, out _, out _, out HTuple winW, out HTuple winH);
                if (partR2.Length > 0 && winH.Length > 0)
                {
                    double imageH = Math.Abs(partR2.D - partR1.D) + 1;
                    fontSize = Math.Max(6, (int)(baseFontSize * winH.D / imageH));
                }
            }
            catch { }

            // 文字显示方向：纵向时用字体 escapement=900 旋转
            // 文字显示：纵向时用换行拼接 (string.Join("\n", ...))，不使用 font escapement 旋转
            try { HOperatorSet.SetFont(hWindow, $"{fontName}-{fontSize}"); } catch { }

            var byGrid = overlays.GroupBy(o => o.GridIndex);
            foreach (var group in byGrid)
            {
                int col = group.Key % gridCols;
                int row = group.Key / gridCols;
                double tileCenterX = col * cropW + cropW / 2.0;
                double tileCenterY = row * cropH + cropH / 2.0;

                double detectX = 0, detectY = 0;
                var grabPos = GrabPositions?.FirstOrDefault(g => g.Index == group.Key);
                if (grabPos != null)
                {
                    detectX = grabPos.X;
                    detectY = grabPos.Y;
                }

                foreach (var item in group)
                {
                    if (_overlayFilter == OverlayResultFilter.OK && !item.IsOK) continue;
                    if (_overlayFilter == OverlayResultFilter.NG && item.IsOK) continue;

                    string color, label;
                    double textPx, textPy;

                    if (item.IsEmptyPin)
                    {
                        // 空针：#NG 标记，文字定位到焊盘位置，h=0
                        string emptyColor = _sysParam.Data.EmptyPinColor;
                        color = string.IsNullOrEmpty(emptyColor) ? "orange" : emptyColor;
                        textPx = tileCenterX + (item.PadMmX - detectX) * pixelsPerMm;
                        textPy = tileCenterY - (item.PadMmY - detectY) * pixelsPerMm;
                        if (_overlayDisplayMode == OverlayDisplayMode.NumberOnly)
                            label = "#NULL";
                        else if (textDir2 == "Vertical")
                            label = "#NULL\nNAN\nNAN\n0";
                        else
                            label = "#NULL (NAN,NAN,0)";
                    }
                    else if (item.IsCrookedPin)
                    {
                        // 歪针：#pinId 标记，文字定位到焊盘位置，dx=dy=NAN，显示3D高度
                        color = "red";
                        textPx = tileCenterX + (item.PadMmX - detectX) * pixelsPerMm;
                        textPy = tileCenterY - (item.PadMmY - detectY) * pixelsPerMm;
                        if (_overlayDisplayMode == OverlayDisplayMode.NumberOnly)
                            label = $"#{item.PinId}";
                        else if (textDir2 == "Vertical")
                            label = $"#{item.PinId}\nNAN\nNAN\n{item.H:F3}";
                        else
                            label = $"#{item.PinId} (NAN,NAN,{item.H:F3})";
                    }
                    else
                    {
                        color = item.IsOK ? "green" : "red";
                        // 所有针的文字标注统一基于焊盘坐标，避免因针尖偏移导致标签重叠
                        textPx = tileCenterX + (item.PadMmX - detectX) * pixelsPerMm;
                        textPy = tileCenterY - (item.PadMmY - detectY) * pixelsPerMm;
                        if (_overlayDisplayMode == OverlayDisplayMode.NumberOnly)
                            label = $"#{item.PinId}";
                        else if (textDir2 == "Vertical")
                            label = $"#{item.PinId}\n{item.Dx:F3}\n{item.Dy:F3}\n{item.H:F3}";
                        else
                            label = $"#{item.PinId}({item.Dx:F3} {item.Dy:F3} {item.H:F3})";
                    }

                    HOperatorSet.SetColor(hWindow, color);

                    string bgColor = _sysParam.Data.TextBgColor;
                    HTuple genName = new HTuple(), genValue = new HTuple();
                    if (!string.IsNullOrEmpty(bgColor))
                    {
                        genName = new HTuple(new string[] { "box", "box_color" });
                        genValue = new HTuple(new string[] { "true", bgColor });
                    }
                    else
                    {
                        genName = new HTuple("box");
                        genValue = new HTuple("false");
                    }

                    try
                    {
                        HOperatorSet.DispText(hWindow, label, "image",
                            textPy + offsetY, textPx + offsetX, color,
                            genName, genValue);
                    }
                    catch { }
                }
            }
        }
        #endregion
    }
}
