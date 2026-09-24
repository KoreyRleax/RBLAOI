using RBLAOI.Core;
using RBLAOI.Core.Managers;
using RBLAOI.Core.Motion;
using RBLAOI.Core.Utility;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace RBLAOI
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            // 启动诊断：全局异常落盘（临时，定位启动即退出问题后移除）
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                System.IO.File.WriteAllText(@"C:\Users\59538\Desktop\RBLAOI\startup_error.txt",
                    "[AppDomain] " + (e.ExceptionObject?.ToString() ?? "null"));
            DispatcherUnhandledException += (s, e) =>
                System.IO.File.WriteAllText(@"C:\Users\59538\Desktop\RBLAOI\startup_error.txt",
                    "[Dispatcher] " + (e.Exception?.ToString() ?? "null") + "\n" + e.Exception?.StackTrace);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 初始化日志服务
            Log.Initialize();
            Log.Info("程序启动", "App");


            // 加载系统参数
            var sysParam = SysParam.Instance;

            // 根据保存的主题加载对应的主题文件
            string themeName = sysParam.CurrentTheme;
            string themePath = $"Styles/{themeName}Theme.xaml";


            try
            {
                var themeDict = new ResourceDictionary
                {
                    Source = new Uri(themePath, UriKind.Relative)
                };

                // 清除默认资源，添加保存的主题
                Resources.MergedDictionaries.Clear();
                Resources.MergedDictionaries.Add(themeDict);
            }
            catch
            {
                // 如果加载失败，默认加载深色主题
                var defaultTheme = new ResourceDictionary
                {
                    Source = new Uri("Styles/DarkTheme.xaml", UriKind.Relative)
                };
                Resources.MergedDictionaries.Add(defaultTheme);
            }

        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Info("程序退出", "App");
            MotionIO.Cleanup();
            base.OnExit(e);
        }
    }
}
