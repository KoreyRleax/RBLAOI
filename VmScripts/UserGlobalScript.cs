using System;
using VM.GlobalScript.Methods;
using iMVS_6000PlatformSDKCS;
using System.Runtime.InteropServices;
using System.Text;
using VM.Core;
using VM.PlatformSDKCS;
using ImageSourceModuleCs;
using System.Drawing;

public class UserGlobalScript : UserGlobalMethods, IScriptMethods
{
    public int Init()
    {
        InitSDK();
        StartGlobalCommunicate();
        RegesiterReceiveCommunicateDataEvent();
        return 0;
    }

    public int Process()
    {
        if (m_operateHandle == IntPtr.Zero)
            return ImvsSdkPFDefine.IMVS_EC_NULL_PTR;
        return DefaultExecuteProcess();
    }

    public override void UserGlobalMethods_OnReceiveCommunicateDataEvent(ReceiveDataInfo dataInfo)
    {
        if (dataInfo == null || dataInfo.DeviceData == null)
            return;

        string str = Encoding.Default.GetString(dataInfo.DeviceData);

        if (str.Contains("SET_PATH:"))
        {
            string path = str.Substring("SET_PATH:".Length);
            SetGlobalVariableStringValue("ImageSavePath", path);
            return;
        }

        if (str.Contains("SET_EXPOSURE:"))
        {
            string val = str.Substring("SET_EXPOSURE:".Length);
            double exposureUs = 0;
            if (double.TryParse(val, out exposureUs))
            {
                object camera = VmSolution.Instance["全局相机1"];
                if (camera != null)
                {
                    object moduParams = camera.GetType().GetProperty("ModuParams").GetValue(camera, null);
                    if (moduParams != null)
                    {
                        CModuleParamBase p = moduParams as CModuleParamBase;
                        if (p != null)
                            p.SetParamValue("ExposureTime", exposureUs.ToString("F0"));
                    }
                    VmSolution.Save();
                }
            }
            return;
        }

        if (str.Contains("DETECT2D_RECT:"))
        {
            string imagePath = str.Substring("DETECT2D_RECT:".Length);

            // 1. 设置流程1图像源路径（先清空再设置，避免VM路径缓存导致读取旧内容）
            ImageSourceModuleTool imgSrc = (ImageSourceModuleTool)
                VmSolution.Instance["RECT检测.图像源1"];
            if (imgSrc != null)
            {
                imgSrc.SetImagePath("");
                imgSrc.SetImagePath(imagePath);
            }

            // 2. 执行检测流程（结果格式化+发送已在方案中完成）
            VmProcedure pro = (VmProcedure)VmSolution.Instance["RECT检测"];
            if (pro != null)
                pro.Run("", false);
            return;
        }

        if (str.Contains("DETECT2D_BLOB:"))
        {
            string dataPart = str.Substring("DETECT2D_BLOB:".Length).Trim();
            string[] segments = dataPart.Split('|');

            // 第一个段 = 板面图路径
            string boardPath = segments.Length > 0 ? segments[0] : "";

            // 设置图像源1（板面图/焊盘图，先清空再设置避免缓存）
            ImageSourceModuleTool imgSrcPad = (ImageSourceModuleTool)
                VmSolution.Instance["BLOB检测.图像源1"];
            if (imgSrcPad != null)
            {
                imgSrcPad.SetImagePath("");
                imgSrcPad.SetImagePath(boardPath);
            }

            // 设置全局变量 isA/isB/isC... 默认全为 0
            string[] markerTypes = { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J" };
            foreach (var m in markerTypes)
                SetGlobalVariableIntValue(string.Concat("is", m), 0);

            // 处理 A:pathA 格式的段（从第1个段开始，跳过第0个=板面图）
            for (int i = 1; i < segments.Length; i++)
            {
                string seg = segments[i];
                if (string.IsNullOrWhiteSpace(seg)) continue;

                int colonIdx = seg.IndexOf(':');
                if (colonIdx <= 0) continue;

                string marker = seg.Substring(0, colonIdx);
                string path = seg.Substring(colonIdx + 1);

                if (string.IsNullOrEmpty(marker) || string.IsNullOrEmpty(path)) continue;

                // 设置 isX = 1
                SetGlobalVariableIntValue(string.Concat("is", marker), 1);

                // 设置对应图像源: A→图像源2, B→图像源3, C→图像源4...
                int imageSourceIndex = 2 + (marker[0] - 'A');
                if (imageSourceIndex >= 2 && imageSourceIndex <= 11)
                {
                    string moduleName = string.Concat("BLOB检测.图像源", imageSourceIndex);
                    ImageSourceModuleTool imgSrc = (ImageSourceModuleTool)
                        VmSolution.Instance[moduleName];
                    if (imgSrc != null)
                    {
                        imgSrc.SetImagePath("");
                        imgSrc.SetImagePath(path);
                    }
                }
            }

            // 执行检测
            VmProcedure proBlob = (VmProcedure)VmSolution.Instance["BLOB检测"];
            if (proBlob != null)
                proBlob.Run("", false);
        }
    }

    public override void ResultDataCallBack(IntPtr outputPlatformInfo, IntPtr puser)
    {
        base.ResultDataCallBack(outputPlatformInfo, puser);
    }

    public override void Dispose()
    {
        base.Dispose();
    }
}
