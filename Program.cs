using System;
using System.Text;
using System.Windows.Forms;

namespace GalaxyAngel2Localization
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // 让 Encoding.GetEncoding(932) 可用（Shift-JIS）
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new UI.MainForm());
        }
    }
}