using System;
using System.Drawing;
using System.ComponentModel;
using System.Windows.Forms;
using System.Threading;
using CyUSB;
using System.Runtime.InteropServices;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using System.Drawing.Imaging;
using ComboBox = System.Windows.Forms.ComboBox;
using Button = System.Windows.Forms.Button;
using System.Text;

namespace Streamer
{

    public class ManualCaliberation : System.Windows.Forms.Form
    {
        bool bVista;

        USBDeviceList usbDevices;
        CyUSBDevice MyDevice;
        CyUSBEndPoint EndPoint;

        byte[] sample = new byte[360960];
        Bitmap bmp;

        CyControlEndPoint CtrlEndPt = null;
        string configFile = Application.StartupPath + "\\config\\sensor.conf";

        //Vendor Commands
        const byte VX_AA = 0xAA;                //Init Vendor Command
        const byte VX_FE = 0xFE;                //Reg Vendor Command
        const byte VX_F5 = 0xF5;                //Start streaming Vendor Command
        const byte VX_FC = 0xFC;                //Lense switch between dark and Light Vendor Command
        const byte VX_F3 = 0xF3;                //Get 32 byte from device Vendor Command
        const byte VX_F4 = 0xF4;                //Set 32 byte to device Vendor Command

                                                //Frame Buffer = Ht*Wd
        static int Ht = 480;                    //int Ht = 480;
        static int Wd = 752;                    //int Wd = 752;
        static int bytes = Wd * Ht;

        double XferBytes;
        
        static byte DefaultBufInitValue = VX_F5;//0xA5;

        int BufSz;
        int QueueSz;
        int PPX;
        int IsoPktBlockSize;
        int Successes;
        int Failures;

        Thread tListen;
        Thread th;
        static bool bRunning;

        // These are  needed for Thread to update the UI
        delegate void UpdateUICallback();
        UpdateUICallback updateUI;
        private ComboBox DevicesComboBox11;
        private ComboBox EndPointsComboBox;
        private Button StartBtn;

        // These are needed to close the app from the Thread exception(exception handling)
        delegate void ExceptionCallback();
        ExceptionCallback handleException;
        private PictureBox SensorView1;
        private Panel panel1;
        private IContainer components;

        Rectangle area;

        int x = 10, y = 10;
        int[,] referencePoints = new int[30, 2];
        int[,] samplePoints = new int[30, 2];
        bool circle = true, isGrid = true;
        int sampleCount = 0;
        static int lastXAxis = 0, lastYAxis = 0;
        int refCount = 0;
        double k, KX1, KX2, KX3, KY1, KY2, KY3;
        private Label label2;
        private Label label1;

        public ManualCaliberation()
        {
            bVista = (Environment.OSVersion.Version.Major < 6) ||
                ((Environment.OSVersion.Version.Major == 6) && Environment.OSVersion.Version.Minor == 0);

            // Required for Windows Form Designer support
            InitializeComponent();

            // Setup the callback routine for updating the UI
            updateUI = new UpdateUICallback(StatusUpdate);

            // Setup the callback routine for NullReference exception handling
            handleException = new ExceptionCallback(ThreadException);

            // Create the list of USB devices attached to the CyUSB3.sys driver.
            usbDevices = new USBDeviceList(CyConst.DEVICES_CYUSB);

            //Assign event handlers for device attachment and device removal.
            usbDevices.DeviceAttached += new EventHandler(usbDevices_DeviceAttached);
            usbDevices.DeviceRemoved += new EventHandler(usbDevices_DeviceRemoved);

            //Set and search the device with VID-PID 04b4-1003 and if found, selects the end point
            SetDevice(false);
        }


        /*Summary
           This is the event handler for device removal. This method resets the device count and searches for the device with 
           VID-PID 04b4-1003
        */
        void usbDevices_DeviceRemoved(object sender, EventArgs e)
        {
            bRunning = false;

            if (tListen != null && tListen.IsAlive == true)
            {
                tListen.Abort();
                tListen.Join();
                tListen = null;
            }

            MyDevice = null;
            EndPoint = null;
            SetDevice(false);

            if (StartBtn.Text.Equals("Start") == false)
            {   
                {
                    DevicesComboBox11.Enabled = true;
                    EndPointsComboBox.Enabled = true;
                    StartBtn.Text = "Start";
                    bRunning = false;

                    /*t2 = DateTime.Now;
                    elapsed = t2 - t1;
                    xferRate = (long)(XferBytes / elapsed.TotalMilliseconds);
                    xferRate = xferRate / (int)100 * (int)100;*/
                    
                    StartBtn.BackColor = Color.Aquamarine;
                }

            }
        }



        /*Summary
           This is the event handler for device attachment. This method  searches for the device with 
           VID-PID 04b4-00F1
        */
        void usbDevices_DeviceAttached(object sender, EventArgs e)
        {
            SetDevice(false);
        }


        /*Summary
           Search the device with VID-PID 04b4-00F1 and if found, select the end point
        */
        private void SetDevice(bool bPreserveSelectedDevice)
        {
            int nCurSelection = 0;
            if (DevicesComboBox11.Items.Count > 0)
            {
                nCurSelection = DevicesComboBox11.SelectedIndex;
                DevicesComboBox11.Items.Clear();
            }
            int nDeviceList = usbDevices.Count;
            for (int nCount = 0; nCount < nDeviceList; nCount++)
            {
                USBDevice fxDevice = usbDevices[nCount];
                String strmsg;
                strmsg = "(0x" + fxDevice.VendorID.ToString("X4") + " - 0x" + fxDevice.ProductID.ToString("X4") + ") " + fxDevice.FriendlyName;
                DevicesComboBox11.Items.Add(strmsg);
            }

            if (DevicesComboBox11.Items.Count > 0 )
                DevicesComboBox11.SelectedIndex = ((bPreserveSelectedDevice == true) ? nCurSelection : 0);

            USBDevice dev = usbDevices[DevicesComboBox11.SelectedIndex];

            if (dev != null)
            {
                MyDevice = (CyUSBDevice)dev;

                GetEndpointsOfNode(MyDevice.Tree);
                //PpxBox.Text = "16"; //Set default value to 8 Packets
                //QueueBox.Text = "8";
                if (EndPointsComboBox.Items.Count > 0)
                {
                    EndPointsComboBox.SelectedIndex = 0;
                    StartBtn.Enabled = true;
                    //start();
                }
                else StartBtn.Enabled = false;

                Text = MyDevice.FriendlyName;
            }
            else
            {
                StartBtn.Enabled = false;
                EndPointsComboBox.Items.Clear();
                EndPointsComboBox.Text = "";
                Text = "C# Streamer - no device";
            }
        }


        /*Summary
           Recursive routine populates EndPointsComboBox with strings 
           representing all the endpoints in the device.
        */
        private void GetEndpointsOfNode(TreeNode devTree)
        {
            //EndPointsComboBox.Items.Clear();
            foreach (TreeNode node in devTree.Nodes)
            {
                if (node.Nodes.Count > 0)
                    GetEndpointsOfNode(node);
                else
                {
                    CyUSBEndPoint ept = node.Tag as CyUSBEndPoint;
                    if (ept == null)
                    {
                        //return;
                    }
                    else if (!node.Text.Contains("Control"))
                    {
                        CyUSBInterface ifc = node.Parent.Tag as CyUSBInterface;
                        string s = string.Format("ALT-{0}, {1} Byte {2}", ifc.bAlternateSetting, ept.MaxPktSize, node.Text);
                        EndPointsComboBox.Items.Add(s);
                    }

                }
            }

        }


        /*Summary
           Clean up any resources being used.
        */
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (components != null)
                {
                    components.Dispose();
                }
            }
            base.Dispose(disposing);
        }


        /*Summary
           Executes on clicking Help->about
        */
        private void AboutItem_Click(object sender, System.EventArgs e)
        {
            string assemblyList = Util.Assemblies;
            MessageBox.Show(assemblyList, Text);
        }



        /*Summary
           Executes on clicking File->Exit
        */
        private void ExitItem_Click(object sender, System.EventArgs e)
        {            
            Close();
        }


        /*Summary
         This is the System event handler.  
         Enforces valid values for PPX(Packet per transfer)
        */
       
        private void DeviceComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            MyDevice = null;
            EndPoint = null;
            SetDevice(true);
       }

        /*Summary
         This is a system event handler, when the selected index changes(end point selection).
        */
        private void EndPointsComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Get the Alt setting
            string sAlt = EndPointsComboBox.Text.Substring(4, 1);
            byte a = Convert.ToByte(sAlt);
            MyDevice.AltIntfc = a;

            // Get the endpoint
            int aX = EndPointsComboBox.Text.LastIndexOf("0x");
            string sAddr = EndPointsComboBox.Text.Substring(aX, 4);
            byte addr = (byte)Util.HexToInt(sAddr);

            EndPoint = MyDevice.EndPointOf(addr);
        }



        /*Summary
          Executes on Start Button click 
        */
        private void StartBtn_Click(object sender, System.EventArgs e)
        {
            start();
        }

        private void start()
        {
            if (MyDevice == null)
                return;


            if (StartBtn.Text.Equals("Start"))
            {
                sendData();

                //DevicesComboBox.Enabled = false;
                EndPointsComboBox.Enabled = false;
                StartBtn.Text = "Stop";
                StartBtn.BackColor = Color.Pink;
                //PpxBox.Enabled = false;
                //QueueBox.Enabled = false;


                BufSz = bytes;//EndPoint.MaxPktSize * Convert.ToUInt16(PpxBox.Text);
                QueueSz = Convert.ToUInt16(8);
                PPX = Convert.ToUInt16(32);

                EndPoint.XferSize = BufSz;

                if (EndPoint is CyIsocEndPoint)
                    IsoPktBlockSize = (EndPoint as CyIsocEndPoint).GetPktBlockSize(BufSz);
                else
                    IsoPktBlockSize = 0;

                bRunning = true;

                tListen = new Thread(new ThreadStart(XferThread));
                tListen.IsBackground = true;
                tListen.Priority = ThreadPriority.Highest;
                tListen.Start();
            }
            else
            {
                if (tListen.IsAlive)
                {
                    //DevicesComboBox.Enabled = true;
                    EndPointsComboBox.Enabled = true;
                    //PpxBox.Enabled = true;
                    //QueueBox.Enabled = true;
                    StartBtn.Text = "Start";
                    bRunning = false;

                    /*t2 = DateTime.Now;
                    elapsed = t2 - t1;
                    xferRate = (long)(XferBytes / elapsed.TotalMilliseconds);
                    xferRate = xferRate / (int)100 * (int)100;*/

                    if (tListen.Join(5000) == false)
                        tListen.Abort();

                    tListen = null;

                    StartBtn.BackColor = Color.Aquamarine;
                }

            }
        }


        /*Summary
          Data Xfer Thread entry point. Starts the thread on Start Button click 
        */
        public unsafe void XferThread()
        {
            // Setup the queue buffers
            byte[][] cmdBufs = new byte[QueueSz][];
            byte[][] xferBufs = new byte[QueueSz][];
            byte[][] ovLaps = new byte[QueueSz][];
            ISO_PKT_INFO[][] pktsInfo = new ISO_PKT_INFO[QueueSz][];

            //int xStart = 0;

            //////////////////////////////////////////////////////////////////////////////
            ///////////////Pin the data buffer memory, so GC won't touch the memory///////
            //////////////////////////////////////////////////////////////////////////////

            GCHandle cmdBufferHandle = GCHandle.Alloc(cmdBufs[0], GCHandleType.Pinned);
            GCHandle xFerBufferHandle = GCHandle.Alloc(xferBufs[0], GCHandleType.Pinned);
            GCHandle overlapDataHandle = GCHandle.Alloc(ovLaps[0], GCHandleType.Pinned);
            GCHandle pktsInfoHandle = GCHandle.Alloc(pktsInfo[0], GCHandleType.Pinned);            

            try
            {
                LockNLoad(cmdBufs, xferBufs, ovLaps, pktsInfo);
            }
            catch (NullReferenceException e)
            {
                // This exception gets thrown if the device is unplugged 
                // while we're streaming data
                e.GetBaseException();
                this.Invoke(handleException);
            }

            //////////////////////////////////////////////////////////////////////////////
            ///////////////Release the pinned memory and make it available to GC./////////
            //////////////////////////////////////////////////////////////////////////////
            cmdBufferHandle.Free();
            xFerBufferHandle.Free();
            overlapDataHandle.Free();
            pktsInfoHandle.Free();
        }


        /*Summary
          This is a recursive routine for pinning all the buffers used in the transfer in memory.
        It will get recursively called QueueSz times.  On the QueueSz_th call, it will call
        XferData, which will loop, transferring data, until the stop button is clicked.
        Then, the recursion will unwind.
        */
        public unsafe void LockNLoad(byte[][] cBufs, byte[][] xBufs, byte[][] oLaps, ISO_PKT_INFO[][] pktsInfo)
        {
            int j = 0;
            int nLocalCount = j;

            GCHandle[] bufSingleTransfer    = new GCHandle[QueueSz];
            GCHandle[] bufDataAllocation    = new GCHandle[QueueSz];
            GCHandle[] bufPktsInfo          = new GCHandle[QueueSz];            
            GCHandle[] handleOverlap        = new GCHandle[QueueSz];

            while (j < QueueSz)
            {
                // Allocate one set of buffers for the queue, Buffered IO method require user to allocate a buffer as a part of command buffer,
                // the BeginDataXfer does not allocated it. BeginDataXfer will copy the data from the main buffer to the allocated while initializing the commands.
                cBufs[j] = new byte[CyConst.SINGLE_XFER_LEN + IsoPktBlockSize + ((EndPoint.XferMode == XMODE.BUFFERED) ? BufSz : 0)];

                xBufs[j] = new byte[BufSz];

                //initialize the buffer with initial value 0xA5
                for (int iIndex = 0; iIndex < BufSz; iIndex++)
                    xBufs[j][iIndex] = DefaultBufInitValue;

                int sz = Math.Max(CyConst.OverlapSignalAllocSize, sizeof(OVERLAPPED));
                oLaps[j] = new byte[sz];
                pktsInfo[j] = new ISO_PKT_INFO[PPX];

                /*/////////////////////////////////////////////////////////////////////////////
                 * 
                 * fixed keyword is getting thrown own by the compiler because the temporary variables 
                 * tL0, tc0 and tb0 aren't used. And for jagged C# array there is no way, we can use this 
                 * temporary variable.
                 * 
                 * Solution  for Variable Pinning:
                 * Its expected that application pin memory before passing the variable address to the
                 * library and subsequently to the windows driver.
                 * 
                 * Cypress Windows Driver is using this very same memory location for data reception or
                 * data delivery to the device.
                 * And, hence .Net Garbage collector isn't expected to move the memory location. And,
                 * Pinning the memory location is essential. And, not through FIXED keyword, because of 
                 * non-usability of temporary variable.
                 * 
                /////////////////////////////////////////////////////////////////////////////*/
                //fixed (byte* tL0 = oLaps[j], tc0 = cBufs[j], tb0 = xBufs[j])  // Pin the buffers in memory
                //////////////////////////////////////////////////////////////////////////////////////////////
                bufSingleTransfer[j] = GCHandle.Alloc(cBufs[j], GCHandleType.Pinned);
                bufDataAllocation[j] = GCHandle.Alloc(xBufs[j], GCHandleType.Pinned);
                bufPktsInfo[j] = GCHandle.Alloc(pktsInfo[j], GCHandleType.Pinned);
                handleOverlap[j] = GCHandle.Alloc(oLaps[j], GCHandleType.Pinned);
                // oLaps "fixed" keyword variable is in use. So, we are good.
                /////////////////////////////////////////////////////////////////////////////////////////////            

                unsafe
                {
                    //fixed (byte* tL0 = oLaps[j])
                    {
                        CyUSB.OVERLAPPED ovLapStatus = new CyUSB.OVERLAPPED();
                        ovLapStatus = (CyUSB.OVERLAPPED)Marshal.PtrToStructure(handleOverlap[j].AddrOfPinnedObject(), typeof(CyUSB.OVERLAPPED));
                        ovLapStatus.hEvent = (IntPtr)PInvoke.CreateEvent(0, 0, 0, 0);
                        Marshal.StructureToPtr(ovLapStatus, handleOverlap[j].AddrOfPinnedObject(), true);

                        // Pre-load the queue with a request
                        int len = BufSz;
                        if (EndPoint.BeginDataXfer(ref cBufs[j], ref xBufs[j], ref len, ref oLaps[j]) == false)
                            Failures++;
                    }
                    j++;
                }
            }

            XferData(cBufs, xBufs, oLaps, pktsInfo, handleOverlap);          // All loaded. Let's go!

            unsafe
            {
                for (nLocalCount = 0; nLocalCount < QueueSz; nLocalCount++)
                {
                    CyUSB.OVERLAPPED ovLapStatus = new CyUSB.OVERLAPPED();
                    ovLapStatus = (CyUSB.OVERLAPPED)Marshal.PtrToStructure(handleOverlap[nLocalCount].AddrOfPinnedObject(), typeof(CyUSB.OVERLAPPED));
                    PInvoke.CloseHandle(ovLapStatus.hEvent);

                    /*////////////////////////////////////////////////////////////////////////////////////////////
                     * 
                     * Release the pinned allocation handles.
                     * 
                    ////////////////////////////////////////////////////////////////////////////////////////////*/
                    bufSingleTransfer[nLocalCount].Free();
                    bufDataAllocation[nLocalCount].Free();
                    bufPktsInfo[nLocalCount].Free();
                    handleOverlap[nLocalCount].Free();

                    cBufs[nLocalCount] = null;
                    xBufs[nLocalCount] = null;
                    oLaps[nLocalCount] = null;
                }
            }
            GC.Collect();
        }

        

        /*Summary
          Called at the end of recursive method, LockNLoad().
          XferData() implements the infinite transfer loop
        */
        public unsafe void XferData(byte[][] cBufs, byte[][] xBufs, byte[][] oLaps, ISO_PKT_INFO[][] pktsInfo, GCHandle[] handleOverlap)
        {
            int k = 0;
            int len = 0;

            Successes = 0;
            Failures = 0;

            XferBytes = 0;
            //t1 = DateTime.Now;
            long nIteration = 0;
            CyUSB.OVERLAPPED ovData = new CyUSB.OVERLAPPED();

            for (; bRunning; )
            {
                nIteration++;
                // WaitForXfer
                unsafe
                {
                    //fixed (byte* tmpOvlap = oLaps[k])
                    {
                        ovData = (CyUSB.OVERLAPPED)Marshal.PtrToStructure(handleOverlap[k].AddrOfPinnedObject(), typeof(CyUSB.OVERLAPPED));
                        if (!EndPoint.WaitForXfer(ovData.hEvent, 500))
                        {
                            EndPoint.Abort();
                            PInvoke.WaitForSingleObject(ovData.hEvent, 500);
                        }
                    }
                }

                if (EndPoint.Attributes == 1)
                {
                    CyIsocEndPoint isoc = EndPoint as CyIsocEndPoint;
                    // FinishDataXfer
                    if (isoc.FinishDataXfer(ref cBufs[k], ref xBufs[k], ref len, ref oLaps[k], ref pktsInfo[k]))
                    {
                        //XferBytes += len;
                        //Successes++;

                        ISO_PKT_INFO[] pkts = pktsInfo[k];

                        for (int j = 0; j < PPX; j++)
                        {
                            if (pkts[j].Status == 0)
                            {
                                XferBytes += pkts[j].Length;

                                Successes++;
                            }
                            else
                                Failures++;

                            pkts[j].Length = 0;
                        }

                    }
                    else
                        Failures++;
                }
                else
                {
                    // FinishDataXfer
                    if (EndPoint.FinishDataXfer(ref cBufs[k], ref xBufs[k], ref len, ref oLaps[k]))
                    {
                        XferBytes += len;
                        Successes++;
                    }
                    else
                        Failures++;
                }

                // Re-submit this buffer into the queue
                len = BufSz;
                if (EndPoint.BeginDataXfer(ref cBufs[k], ref xBufs[k], ref len, ref oLaps[k]) == false)
                    Failures++;


                //sample = xBufs[k];

                bmp = new Bitmap(Wd, Ht, PixelFormat.Format8bppIndexed);

                ColorPalette ncp = bmp.Palette;
                for (int i = 0; i < 256; i++)
                    ncp.Entries[i] = Color.FromArgb(255, i, i, i);
                bmp.Palette = ncp;

                // create a bitmapdata and lock all pixels to be written 
                BitmapData bmpdata = bmp.LockBits(
                                    new Rectangle(0, 0, Wd, Ht),
                                    ImageLockMode.WriteOnly, bmp.PixelFormat);
                // copy the data from the byte array into bitmapdata.scan0
                Marshal.Copy(xBufs[k], 0, bmpdata.Scan0, xBufs[k].Length);

                // unlock the pixels
                bmp.UnlockBits(bmpdata);

                // do something with your image, e.g. save it to disc
                

                k++;
                if (k == QueueSz)  // Only update displayed stats once each time through the queue
                {
                    k = 0;

                    /*t2 = DateTime.Now;
                    elapsed = t2 - t1;

                    xferRate = (long)(XferBytes / elapsed.TotalMilliseconds);
                    xferRate = xferRate / (int)100 * (int)100;*/


                    // Call StatusUpdate() in the main thread
                    if (bRunning == true) this.Invoke(updateUI);

                    // For small QueueSz or PPX, the loop is too tight for UI thread to ever get service.   
                    // Without this, app hangs in those scenarios.
                    Thread.Sleep(0);
                }
                Thread.Sleep(0);

            } // End infinite loop
            // Let's recall all the queued buffer and abort the end point.
            EndPoint.Abort();            
        }
        
        /*Summary
          The callback routine delegated to updateUI.
        */
        public void StatusUpdate()
        {
            showDataAsImage();
        }


        /*Summary
          The callback routine delegated to handleException.
        */
        public void ThreadException()
        {
            StartBtn.Text = "Start";
            bRunning = false;
            tListen = null;
            StartBtn.BackColor = Color.Aquamarine;
        }

        private void PerfTimer_Tick(object sender, EventArgs e)
        {

        }

        private void sendData()
        {
            if (MyDevice != null)     //Enter this loop only if device is attached
            {
                //Assigning the object
                CtrlEndPt = MyDevice.ControlEndPt;
                int len = 0;
                byte[] bufBegin = new byte[] { 0x00, 0x00, 0x00 };

                //Vendor Command Format : 0xAA to configure the Image Sensor and add the Header
                CtrlEndPt.Target = CyConst.TGT_DEVICE;
                CtrlEndPt.ReqType = CyConst.REQ_VENDOR;
                CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE;
                CtrlEndPt.ReqCode = VX_AA;                               // to configure Image sensor
                CtrlEndPt.Value = 0;
                CtrlEndPt.Index = 0;
                CtrlEndPt.XferData(ref bufBegin, ref len);              //asking for the 512 bytes buffer containing the 5 bytes Header
                                                                        // Thread.Sleep(20);
                                                                        // Open the file for reading
                using (StreamReader sr = new StreamReader(configFile))
                {
                    string line;

                    // Read and display lines until the end of the file
                    while ((line = sr.ReadLine()) != null)
                    {
                        string[] splitted = line.Split(',');
                        string first = splitted[0].Trim();
                        string Second = splitted[1].Trim();

                        MatchCollection matches = Regex.Matches(first, @"\b0x[0-9A-Fa-f]+\b");

                        string numericPhone = new String(Second.TakeWhile(Char.IsDigit).ToArray());
                        //string numericPhone = new string(Second.Take(6).ToArray());
                        int decimalValue = Convert.ToInt32(numericPhone, 10);
                        // Process each line here
                        // Find decimal numbers in the line using regular expressions
                        //MatchCollection matches = Regex.Matches(line, @"\b\d+\b");

                        // Convert decimal numbers to hexadecimal
                        foreach (Match match in matches)
                        {
                            string cap = match.Value.Substring(2);
                            int dec = Convert.ToInt32(cap, 16);
                            //string hexValue = decimalValue.ToString("X");
                            bufBegin[0] = Convert.ToByte(dec & 0x00FF);
                            bufBegin[1] = Convert.ToByte((decimalValue & 0xFF00) >> 8);
                            bufBegin[2] = Convert.ToByte(decimalValue & 0x00FF);
                            len = 0X03;
                            //Vendor Command Format : 0xFE to configure the Image Sensor and add the Header
                            CtrlEndPt.Target = CyConst.TGT_DEVICE;
                            CtrlEndPt.ReqType = CyConst.REQ_VENDOR;
                            CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE;
                            CtrlEndPt.ReqCode = VX_FE;                               // to configure Image sensor
                            CtrlEndPt.Value = 0;
                            CtrlEndPt.Index = 0;
                            CtrlEndPt.XferData(ref bufBegin, ref len);
                            //statusBar1.Text = bufBegin.ToString();
                        }
                        // Console.WriteLine(line);
                    }
                    //Vendor Command Format : 0xF5 to start stream from the Image Sensor and add the Header
                    len = 1;
                    bufBegin[0] = 0x01;
                    CtrlEndPt.Target = CyConst.TGT_DEVICE;
                    CtrlEndPt.ReqType = CyConst.REQ_VENDOR;
                    CtrlEndPt.Direction = CyConst.DIR_FROM_DEVICE;
                    CtrlEndPt.ReqCode = VX_F5;                               // to configure Image sensor
                    CtrlEndPt.Value = 0;
                    CtrlEndPt.Index = 0;
                    CtrlEndPt.XferData(ref bufBegin, ref len);

                    Thread.Sleep(0);
                }

                bytes = (Wd * Ht);
            }
        }

        
        private void showDataAsImage()
        {
            try
            {
                SensorView1.Image = null;

                /*Bitmap bmp = new Bitmap(Wd, Ht, PixelFormat.Format8bppIndexed);

                ColorPalette ncp = bmp.Palette;
                for (int i = 0; i < 256; i++)
                    ncp.Entries[i] = Color.FromArgb(255, i, i, i);
                bmp.Palette = ncp;

                // create a bitmapdata and lock all pixels to be written 
                BitmapData bmpdata = bmp.LockBits(
                                    new Rectangle(0, 0, Wd, Ht),
                                    ImageLockMode.WriteOnly, bmp.PixelFormat);
                // copy the data from the byte array into bitmapdata.scan0
                Marshal.Copy(sample, 0, bmpdata.Scan0, sample.Length);

                // unlock the pixels
                bmp.UnlockBits(bmpdata*/

                // do something with your image, e.g. save it to disc
                bmp.Save("C:\\Users\\" + Environment.UserName + "\\Videos\\img.jpg", ImageFormat.Jpeg);

                string co_ordinates = string.Empty;
                int xAxis = 0, yAxis = 0, count = 0;

                for (int x = 0; x < Wd; x++)
                {
                    for (int y = 0; y < Ht; y++)
                    {
                        try
                        {
                            if (bmp.GetPixel(x, y).ToArgb() == Color.White.ToArgb())
                            {
                                this.Cursor = Cursors.WaitCursor;
                                xAxis += x;
                                yAxis += y;
                                count++;
                                if (sampleCount > 0)
                                {
                                    int diffX = xAxis - lastXAxis, diffY = yAxis - lastYAxis;
                                    MessageBox.Show(diffX + "," + diffY);

                                    if (sampleCount == 5 || sampleCount == 10 || sampleCount == 15 || sampleCount == 20 || sampleCount == 25)
                                    {
                                        lastYAxis = samplePoints[sampleCount - 5, 1];
                                        lastXAxis = samplePoints[sampleCount - 5, 0];

                                        diffX = xAxis - lastXAxis;
                                        diffY = yAxis - lastYAxis;

                                        if (diffX < 90&& diffX > 30 && diffY < 30 && diffY > -15)
                                        {
                                            start();
                                            goto Execute;
                                        }
                                    }
                                    else if (diffX < 30 && diffX > -15 && diffY < 90 && diffY > 30)
                                    {
                                        start();
                                        goto Execute;
                                    }
                                }
                                else if(sampleCount==0)
                                {
                                    MessageBox.Show(x + "," + y);
                                    if (xAxis > 80  && xAxis < 180 && yAxis > 80 && yAxis < 130)
                                    {
                                        start();
                                        goto Execute;
                                    }
                                }
                                goto Show;
                            }
                        }
                        catch (Exception e)
                        {
                            MessageBox.Show(e.Message + "\r\n" + x + "," + y);
                        }
                    }
                }

                Execute:
                if (count > 0)
                {
                    /*xAxis = xAxis / count;
                    yAxis = yAxis / count;*/
                    //MessageBox.Show(x + "," + y);
                    lastXAxis = xAxis;
                    lastYAxis = yAxis;
                    samplePoints[sampleCount, 0] = xAxis;
                    samplePoints[sampleCount, 1] = yAxis;
                    sampleCount++;

                    circle = false;
                    Panel1_Paint(null, null);

                    //start();

                    if (refCount < 30)
                    {
                        this.x = referencePoints[refCount, 0];
                        this.y = referencePoints[refCount, 1];
                        Panel1_Paint(null, null);
                        refCount++;
                        if (DevicesComboBox11.Items.Count > 0 && EndPointsComboBox.Items.Count > 0)
                        {
                            start();
                        }
                    }
                    else if (refCount == 30)
                    {
                        calculateCalibrationCoefficient();
                    }
                    // MessageBox.Show(xAxis + "," + yAxis);
                }
                //SensorView.SizeMode = PictureBoxSizeMode.Zoom;
                Show:
                
                this.Cursor = Cursors.Default;
                SensorView1.Image = null;
               
            }
            catch (Exception ex)
            {
                //MessageBox.Show(ex.ToString() + "\r\n" + "Inner:" + ex.InnerException);
            }
        }

        bool lensemode = false;

        private void button1_Click(object sender, EventArgs e)
        {
            lensSwitch();
        }

        private void InitializeComponent()
        {
            this.EndPointsComboBox = new System.Windows.Forms.ComboBox();
            this.StartBtn = new System.Windows.Forms.Button();
            this.DevicesComboBox11 = new System.Windows.Forms.ComboBox();
            this.SensorView1 = new System.Windows.Forms.PictureBox();
            this.panel1 = new System.Windows.Forms.Panel();
            this.label2 = new System.Windows.Forms.Label();
            this.label1 = new System.Windows.Forms.Label();
            ((System.ComponentModel.ISupportInitialize)(this.SensorView1)).BeginInit();
            this.panel1.SuspendLayout();
            this.SuspendLayout();
            // 
            // EndPointsComboBox
            // 
            this.EndPointsComboBox.DropDownHeight = 120;
            this.EndPointsComboBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.EndPointsComboBox.FormattingEnabled = true;
            this.EndPointsComboBox.IntegralHeight = false;
            this.EndPointsComboBox.Location = new System.Drawing.Point(133, 116);
            this.EndPointsComboBox.Name = "EndPointsComboBox";
            this.EndPointsComboBox.Size = new System.Drawing.Size(432, 32);
            this.EndPointsComboBox.TabIndex = 4;
            this.EndPointsComboBox.SelectedIndexChanged += new System.EventHandler(this.EndPointsComboBox_SelectedIndexChanged);
            // 
            // StartBtn
            // 
            this.StartBtn.BackColor = System.Drawing.Color.Aquamarine;
            this.StartBtn.Location = new System.Drawing.Point(335, 191);
            this.StartBtn.Name = "StartBtn";
            this.StartBtn.Size = new System.Drawing.Size(230, 53);
            this.StartBtn.TabIndex = 5;
            this.StartBtn.Text = "Start";
            this.StartBtn.UseVisualStyleBackColor = false;
            this.StartBtn.Click += new System.EventHandler(this.StartBtn_Click);
            // 
            // DevicesComboBox11
            // 
            this.DevicesComboBox11.DropDownHeight = 120;
            this.DevicesComboBox11.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.DevicesComboBox11.FormattingEnabled = true;
            this.DevicesComboBox11.IntegralHeight = false;
            this.DevicesComboBox11.Location = new System.Drawing.Point(133, 40);
            this.DevicesComboBox11.Name = "DevicesComboBox11";
            this.DevicesComboBox11.Size = new System.Drawing.Size(432, 32);
            this.DevicesComboBox11.TabIndex = 6;
            this.DevicesComboBox11.SelectionChangeCommitted += new System.EventHandler(this.DevicesComboBox11_SelectionChangeCommitted);
            // 
            // SensorView1
            // 
            this.SensorView1.Location = new System.Drawing.Point(651, 40);
            this.SensorView1.Name = "SensorView1";
            this.SensorView1.Size = new System.Drawing.Size(1180, 710);
            this.SensorView1.TabIndex = 7;
            this.SensorView1.TabStop = false;
            // 
            // panel1
            // 
            this.panel1.Controls.Add(this.label2);
            this.panel1.Controls.Add(this.label1);
            this.panel1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.panel1.Location = new System.Drawing.Point(0, 0);
            this.panel1.Name = "panel1";
            this.panel1.Size = new System.Drawing.Size(1891, 1002);
            this.panel1.TabIndex = 8;
            this.panel1.Click += new System.EventHandler(this.panel1_Click);
            // 
            // label2
            // 
            this.label2.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.label2.AutoSize = true;
            this.label2.BackColor = System.Drawing.Color.Transparent;
            this.label2.Font = new System.Drawing.Font("Segoe UI", 11.14286F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label2.ForeColor = System.Drawing.SystemColors.ControlDarkDark;
            this.label2.Location = new System.Drawing.Point(840, 499);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(234, 37);
            this.label2.TabIndex = 1;
            this.label2.Text = "Press Esc to cancel";
            // 
            // label1
            // 
            this.label1.Anchor = System.Windows.Forms.AnchorStyles.None;
            this.label1.AutoSize = true;
            this.label1.BackColor = System.Drawing.Color.Transparent;
            this.label1.Font = new System.Drawing.Font("Segoe UI", 11.14286F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label1.ForeColor = System.Drawing.SystemColors.ControlDarkDark;
            this.label1.Location = new System.Drawing.Point(831, 469);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(258, 37);
            this.label1.TabIndex = 0;
            this.label1.Text = "Manual Caliberation";
            // 
            // ManualCaliberation
            // 
            this.ClientSize = new System.Drawing.Size(1891, 1002);
            this.Controls.Add(this.panel1);
            this.Controls.Add(this.SensorView1);
            this.Controls.Add(this.DevicesComboBox11);
            this.Controls.Add(this.EndPointsComboBox);
            this.Controls.Add(this.StartBtn);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            this.KeyPreview = true;
            this.Name = "ManualCaliberation";
            this.WindowState = System.Windows.Forms.FormWindowState.Maximized;
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.ManualCaliberation_FormClosing);
            this.Load += new System.EventHandler(this.ManualCaliberation_Load);
            this.KeyDown += new System.Windows.Forms.KeyEventHandler(this.ManualCaliberation_KeyDown);
            ((System.ComponentModel.ISupportInitialize)(this.SensorView1)).EndInit();
            this.panel1.ResumeLayout(false);
            this.panel1.PerformLayout();
            this.ResumeLayout(false);

        }

        private void ManualCaliberation_Load(object sender, EventArgs e)
        {
            if (EndPointsComboBox.Items.Count > 0)
                EndPointsComboBox.SelectedIndex = 0;

            panel1.Paint += Panel1_Paint;
            caliberate();
        }

       
        public void caliberate()
        {

            ManualCaliberation myForm = new ManualCaliberation();
            Screen myScreen = Screen.FromControl(myForm);
            area = myScreen.WorkingArea;

            int width = area.Width;
            int height = area.Height;
            int xRefCoordinate = 10, yRefCoordinate = 10;

            int[] x = new int[6], y = new int[5];
            x[0] = 10; y[0] = 10;

            int distBetTwoPointsX = (width - 30) / (x.Length - 1);
            int distBetTwoPointsY = (height - 10) / (y.Length - 1);


            for (int i = 1; i < x.Length; i++)
            {
                xRefCoordinate += distBetTwoPointsX;
                x[i] = xRefCoordinate;
            }
            for (int i = 1; i < y.Length; i++)
            {
                yRefCoordinate += distBetTwoPointsY;
                y[i] = yRefCoordinate;
            }

            int k = 0;
            for (int i = 0; i < x.Length; i++)
            {
                for (int j = 0; j < y.Length; j++)
                {
                    referencePoints[k, 0] = x[i];
                    referencePoints[k, 1] = y[j];
                    k++;
                }
            }

            panel1.BringToFront();

            if (refCount < 30)
            {
                this.x = referencePoints[refCount, 0];
                this.y = referencePoints[refCount, 1];
                
                Panel1_Paint(null, null);
                refCount++;
                if (DevicesComboBox11.Items.Count > 0 && EndPointsComboBox.Items.Count > 0)
                {
                    start();
                }
            }
            else if (refCount == 30)
            {
                calculateCalibrationCoefficient();
            }
        }

        private void ManualCaliberation_FormClosing(object sender, FormClosingEventArgs e)
        {
            bRunning = false;
            if (tListen != null && tListen.IsAlive == true)
            {
                tListen.Abort();
                tListen.Join();
                tListen = null;
            }


            if (usbDevices != null)
                usbDevices.Dispose();
        }

        private void Panel1_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = this.panel1.CreateGraphics();

            if (isGrid)
            {
                int cols = 6;
                int rows = 5;
                int width = panel1.Width / cols;
                int height = panel1.Height / rows;
                Pen p = new Pen(Color.LightGray);
                for (int col = 1; col < cols; col++)
                {
                    g.DrawLine(p, new Point(col * width, 0), new Point(col * width, panel1.Height));
                }
                for (int row = 1; row < rows; row++)
                {
                    g.DrawLine(p, new Point(0, row * height), new Point(panel1.Width, row * height));
                }
            }

            if (circle)
            {
                Pen p = new Pen(Color.Blue);
                g.DrawEllipse(p, this.x, this.y, 13, 13);
                if (refCount == 0)
                {
                    panel1.Invalidate();
                }
            }
            else
            {
                Image img = new Bitmap(Properties.Resources.check_mark);
                g.DrawImage(img, this.x-5, this.y-5);
                circle=true;
                this.Cursor = Cursors.Default;
            }
            
        }

        
        private void panel1_Click(object sender, EventArgs e)
        {
            
        }

        private void ManualCaliberation_KeyDown(object sender, KeyEventArgs e)
        {
            if(e.KeyCode == Keys.Escape)
            {
                start();
                this.Close();
                th = new Thread(openNewForm);
                th.SetApartmentState(ApartmentState.STA);
                th.Start();
            }
        }

        private void calculateCalibrationCoefficient() // calculate the coefficients for calibration algorithm: KX1, KX2, KX3, KY1, KY2, KY3
        {
            int i;
            int Points = 30;
            double[] a = new double[3], b = new double[3], c = new double[3], d = new double[3];
            
            if (Points > 3)
            {
                for (i = 0; i < 3; i++)
                {
                    a[i] = 0;
                    b[i] = 0;
                    c[i] = 0;
                    d[i] = 0;
                }
                for (i = 0; i < Points; i++)
                {
                    a[2] = a[2] + (double)(samplePoints[i,0]);
                    b[2] = b[2] + (double)(samplePoints[i,1]);
                    c[2] = c[2] + (double)(referencePoints[i,0]);
                    d[2] = d[2] + (double)(referencePoints[i,1]);
                    a[0] = a[0] + (double)(samplePoints[i,0]) * (double)(samplePoints[i,0]);
                    a[1] = a[1] + (double)(samplePoints[i,0]) * (double)(samplePoints[i,1]);
                    b[0] = a[1];
                    b[1] = b[1] + (double)(samplePoints[i,1]) * (double)(samplePoints[i,1]);
                    c[0] = c[0] + (double)(samplePoints[i,0]) * (double)(referencePoints[i,0]);
                    c[1] = c[1] + (double)(samplePoints[i,1]) * (double)(referencePoints[i,0]);
                    d[0] = d[0] + (double)(samplePoints[i,0]) * (double)(referencePoints[i,1]);
                    d[1] = d[1] + (double)(samplePoints[i,1]) * (double)(referencePoints[i,1]);
                }
                a[0] = a[0] / a[2];
                a[1] = a[1] / b[2];
                b[0] = b[0] / a[2];
                b[1] = b[1] / b[2];
                c[0] = c[0] / a[2];
                c[1] = c[1] / b[2];
                d[0] = d[0] / a[2];
                d[1] = d[1] / b[2];
                a[2] = a[2] / Points;
                b[2] = b[2] / Points;
                c[2] = c[2] / Points;
                d[2] = d[2] / Points;
            }
            k = (a[0] - a[2]) * (b[1] - b[2]) - (a[1] - a[2]) * (b[0] - b[2]);
            KX1 = ((c[0] - c[2]) * (b[1] - b[2]) - (c[1] - c[2]) * (b[0] - b[2])) / k;
            KX2 = ((c[1] - c[2]) * (a[0] - a[2]) - (c[0] - c[2]) * (a[1] - a[2])) / k;
            KX3 = (b[0] * (a[2] * c[1] - a[1] * c[2]) + b[1] * (a[0] * c[2] - a[2] * c[0]) + b[2] * (a[1] * c[0] -
            a[0] * c[1])) / k;
            KY1 = ((d[0] - d[2]) * (b[1] - b[2]) - (d[1] - d[2]) * (b[0] - b[2])) / k;
            KY2 = ((d[1] - d[2]) * (a[0] - a[2]) - (d[0] - d[2]) * (a[1] - a[2])) / k;
            KY3 = (b[0] * (a[2] * d[1] - a[1] * d[2]) + b[1] * (a[0] * d[2] - a[2] * d[0]) + b[2] * (a[1] * d[0] -
            a[0] * d[1])) / k;

            saveCoeffsToTextFile();
        }

        private void saveCoeffsToTextFile()
        {
            
            string fileName = @"C:\Users\" + Environment.UserName + @"\Music\Roombr.txt";

            try
            {
                // Check if file already exists. If yes, delete it.
                if (File.Exists(fileName))
                {
                    File.Delete(fileName);
                }

                // Create a new file
                using (FileStream fs = File.Create(fileName))
                {
                    // Add some text to file
                    byte[] title = new UTF8Encoding(true).GetBytes("KX1=" + KX1 + "," + "KX2=" + KX2 + "," + "KX3=" + KX3 + "," +
                        "KY1=" + KY1 + "," + "KY2=" + KY2 + "," + "KY3=" + KY3);
                    fs.Write(title, 0, title.Length);
                }
                this.Close();
                th = new Thread(openNewForm);
                th.SetApartmentState(ApartmentState.STA);
                th.Start();
            }
            catch (Exception ex)
            {
                
            }
        }

        private void openNewForm()
        {
            Application.Run(new Form1());
        }

        private void DevicesComboBox11_SelectionChangeCommitted(object sender, EventArgs e)
        {
            MyDevice = null;
            EndPoint = null;
            SetDevice(true);
        }

        private void lensSwitch()
        {
            byte[] bufBegin2 = new byte[] { 0x01 };
            int len = 1;
            if (lensemode == false)
            {
                CtrlEndPt.Target = CyConst.TGT_DEVICE;
                CtrlEndPt.ReqType = CyConst.REQ_VENDOR;
                CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE;
                CtrlEndPt.ReqCode = VX_FC;
                CtrlEndPt.Value = 0x0040;
                CtrlEndPt.Index = 0;
                CtrlEndPt.XferData(ref bufBegin2, ref len);
                Thread.Sleep(50);
                bufBegin2[0] = 0x00;
                CtrlEndPt.Target = CyConst.TGT_DEVICE;
                CtrlEndPt.ReqType = CyConst.REQ_VENDOR;
                CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE;
                CtrlEndPt.ReqCode = VX_FC;
                CtrlEndPt.Value = 0x0040;
                CtrlEndPt.Index = 0;
                CtrlEndPt.XferData(ref bufBegin2, ref len);

                lensemode = true;
            }
            else
            {
                CtrlEndPt.Target = CyConst.TGT_DEVICE;
                CtrlEndPt.ReqType = CyConst.REQ_VENDOR;
                CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE;
                CtrlEndPt.ReqCode = VX_FC;
                CtrlEndPt.Value = 0x0080;
                CtrlEndPt.Index = 0;
                CtrlEndPt.XferData(ref bufBegin2, ref len);
                Thread.Sleep(50);
                bufBegin2[0] = 0x00;
                CtrlEndPt.Target = CyConst.TGT_DEVICE;
                CtrlEndPt.ReqType = CyConst.REQ_VENDOR;
                CtrlEndPt.Direction = CyConst.DIR_TO_DEVICE;
                CtrlEndPt.ReqCode = VX_FC;
                CtrlEndPt.Value = 0x0080;
                CtrlEndPt.Index = 0;
                CtrlEndPt.XferData(ref bufBegin2, ref len);

                lensemode = false;
            }
        }

    }
}
