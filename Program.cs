using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Streamer
{
    internal class Program
    {
        [STAThread]
        static void Main()
        {
            try
            {
                Application.Run(new Form1());
                //Application.Run(new ManualCaliberation());
            }
            catch (Exception e)
            {
                MessageBox.Show(e.StackTrace, "Exception '" + e.Message + "' thrown by " + e.Source);
            }
        }
    }
}
