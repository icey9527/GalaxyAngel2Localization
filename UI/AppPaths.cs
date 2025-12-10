using System;
using System.Diagnostics;
using System.IO;

namespace GalaxyAngel2Localization.UI
{
    internal static class AppPaths
    {
        /// <summary>
        /// 程序实际所在目录（发布单文件时也是 exe 所在目录，而不是 .net 临时目录）
        /// </summary>
        public static readonly string AppRoot =
            Path.GetDirectoryName(Environment.ProcessPath
                                  ?? Process.GetCurrentProcess().MainModule!.FileName)
            ?? AppContext.BaseDirectory;
    }
}