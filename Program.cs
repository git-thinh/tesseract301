using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Linq;
using Newtonsoft.Json;
using OCR.TesseractWrapper;

class Program
{
    static int m_port_write = 0;
    static int m_port_read = 0;

    #region [ CODE ]

    static RedisBase m_subcriber;
    static bool _subscribe(string channel)
    {
        if (string.IsNullOrEmpty(channel)) return false;
        channel = "<{" + channel + "}>";
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("*2\r\n");
            sb.Append("$10\r\nPSUBSCRIBE\r\n");
            sb.AppendFormat("${0}\r\n{1}\r\n", channel.Length, channel);

            byte[] buf = Encoding.UTF8.GetBytes(sb.ToString());
            var ok = m_subcriber.SendBuffer(buf);
            var lines = m_subcriber.ReadMultiString();
            //Console.WriteLine("\r\n\r\n{0}\r\n\r\n", string.Join(Environment.NewLine, lines));
            return ok;
        }
        catch (Exception ex)
        {
        }
        return false;
    }

    static oTesseractRequest __ocrExecute(oTesseractRequest req, Bitmap bitmap, RedisBase redis)
    {
        using (TesseractProcessor processor = new TesseractProcessor())
        {
            processor.InitForAnalysePage();
            //processor.SetPageSegMode(ePageSegMode.PSM_AUTO_ONLY);
            //var success = processor.Init(req.data_path, req.lang, (int)eOcrEngineMode.OEM_DEFAULT);

            var imageColor = new Emgu.CV.Mat();
            //var imageCV = new Emgu.CV.Image<Emgu.CV.Structure.Bgr, byte>(bitmap); //Image Class from Emgu.CV
            var imageCV = new Emgu.CV.Image<Emgu.CV.Structure.Gray, byte>(bitmap); //Image Class from Emgu.CV
            var image = imageCV.Mat; //This is your Image converted to Mat

            if (image.NumberOfChannels == 1)
                Emgu.CV.CvInvoke.CvtColor(image, imageColor, Emgu.CV.CvEnum.ColorConversion.Gray2Bgr);
            else
                image.CopyTo(imageColor);

            //using (Bitmap bmp = Bitmap.FromFile(@"C:\temp\1.jpg") as Bitmap)
            using (Bitmap bmp = imageColor.Bitmap)
            {
                DocumentLayout doc = null;
                switch (req.command)
                {
                    case TESSERACT_COMMAND.GET_TEXT:
                        //string s = tes.GetText().Trim();
                        //req.output_text = s;
                        //req.output_count = s.Length;
                        req.ok = 1;
                        break;
                    default:
                        unsafe
                        {
                            doc = processor.AnalyseLayout(bmp);
                        }
                        if (doc != null)
                        {
                            var bs = new List<string>();
                            if (doc.Blocks.Count > 0)
                            {
                                for (int i = 0; i < doc.Blocks.Count; i++)
                                    for (int j = 0; j < doc.Blocks[i].Paragraphs.Count; j++)
                                        bs.AddRange(doc.Blocks[j].Paragraphs[j].Lines
                                            .Select(x => string.Format(
                                                "{0}_{1}_{2}_{3}", x.Left, x.Top, x.Right, x.Bottom)));
                            }
                            req.output_text = string.Join("|", bs.ToArray());
                            req.output_count = bs.Count;
                            req.ok = 1;
                        }
                        break;
                }
            }
        }

        return req;
    }

    static void __executeBackground(byte[] buf)
    {
        oTesseractRequest r = null;
        string guid = Encoding.ASCII.GetString(buf);
        var redis = new RedisBase(new RedisSetting(REDIS_TYPE.ONLY_READ, 1000));
        try
        {
            string json = redis.HGET("_OCR_REQUEST", guid);
            r = JsonConvert.DeserializeObject<oTesseractRequest>(json);
            Bitmap bitmap = redis.HGET_BITMAP(r.redis_key, r.redis_field);
            if (bitmap != null)
                r = __ocrExecute(r, bitmap, redis);
        }
        catch (Exception ex)
        {
            if (r != null)
            {
                string error = ex.Message + Environment.NewLine + ex.StackTrace
                    + Environment.NewLine + "----------------" + Environment.NewLine +
                   JsonConvert.SerializeObject(r);
                r.ok = -1;
                redis.HSET("_OCR_REQ_ERR", r.requestId, error);
            }
        }

        if (r != null)
        {
            redis.HSET("_OCR_REQUEST", r.requestId, JsonConvert.SerializeObject(r, Formatting.Indented));
            redis.HSET("_OCR_REQ_LOG", r.requestId, r.ok.ToString());
            redis.PUBLISH("__TESSERACT_OUT", r.requestId);
        }
    }

    #endregion

    static void __startApp()
    {
        if (m_port_write == 0) m_port_write = 1000;
        if (m_port_read == 0) m_port_read = 1001;
        m_subcriber = new RedisBase(new RedisSetting(REDIS_TYPE.ONLY_SUBCRIBE, 1001));
        _subscribe("__TESSERACT_IN");

        var bs = new List<byte>();
        while (__running)
        {
            if (!m_subcriber.m_stream.DataAvailable)
            {
                if (bs.Count > 0)
                {
                    var buf = m_subcriber.__getBodyPublish("__TESSERACT_IN", bs.ToArray());
                    bs.Clear();
                    new Thread(new ParameterizedThreadStart((o)
                        => __executeBackground((byte[])o))).Start(buf);
                }

                Thread.Sleep(100);
                continue;
            }

            byte b = (byte)m_subcriber.m_stream.ReadByte();
            bs.Add(b);
        }
    }

    static bool __running = true;
    static void __stopApp() => __running = false;

    #region [ SETUP WINDOWS SERVICE ]

    static Thread __threadWS = null;
    static void Main(string[] args)
    {
        if (Environment.UserInteractive)
        {
            StartOnConsoleApp(args);
            Console.WriteLine("Press any key to stop...");
            Console.ReadKey(true);
            Stop();
        }
        else using (var service = new MyService())
                ServiceBase.Run(service);
    }

    public static void StartOnConsoleApp(string[] args) => __startApp();
    public static void StartOnWindowService(string[] args)
    {
        __threadWS = new Thread(new ThreadStart(() => __startApp()));
        __threadWS.IsBackground = true;
        __threadWS.Start();
    }

    public static void Stop()
    {
        __stopApp();
        if (__threadWS != null) __threadWS.Abort();
    }

    #endregion;
}

