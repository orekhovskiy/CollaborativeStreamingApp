using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AForge.Video.DirectShow;
namespace WinFormsApp1
{
    static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FilterInfoCollection filterInfoCollection = new FilterInfoCollection(FilterCategory.VideoInputDevice);
            Debugger.Log(0, "", $"Web cam's number: {filterInfoCollection.Count}\n");
            foreach (FilterInfo device in filterInfoCollection)
            {
                Debugger.Log(0, "", $"Web cam : {device.Name}\n");
            }

            Application.Run(new Form1());
        }
    }
}
