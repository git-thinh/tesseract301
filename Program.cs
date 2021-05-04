using System;
using System.Collections.Generic;
using System.Drawing;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Linq;
using Newtonsoft.Json;
using OCR.TesseractWrapper;
using IPoVn.IPCore;
using System.Drawing.Imaging;

class Program
{
    const int __PORT_WRITE = 1000;
    const int __PORT_READ = 1001;
    static RedisBase m_subcriber;
    static bool __running = true;

    static oTesseractRequest __ocrExecute(oTesseractRequest req, Bitmap bitmap)
    {
        using (TesseractProcessor processor = new TesseractProcessor())
        {
            processor.InitForAnalysePage();
            //processor.SetPageSegMode(ePageSegMode.PSM_AUTO_ONLY);
            //var success = processor.Init(req.data_path, req.lang, (int)eOcrEngineMode.OEM_DEFAULT);

            //var imageColor = new Emgu.CV.Mat();
            //var imageCV = new Emgu.CV.Image<Emgu.CV.Structure.Bgr, byte>(bitmap); //Image Class from Emgu.CV
            ////var imageCV = new Emgu.CV.Image<Emgu.CV.Structure.Gray, byte>(bitmap); //Image Class from Emgu.CV
            //var image = imageCV.Mat; //This is your Image converted to Mat

            //if (image.NumberOfChannels == 1)
            //    Emgu.CV.CvInvoke.CvtColor(image, imageColor, Emgu.CV.CvEnum.ColorConversion.Gray2Bgr);
            //else
            //    image.CopyTo(imageColor);

            //using (Bitmap bmp = Bitmap.FromFile(@"C:\temp\1.jpg") as Bitmap)
            using (Bitmap bmp = bitmap.Clone() as Bitmap)
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
                            req.output_format = "left_top_right_bottom";
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

    static oTesseractRequest __ocrExecute2(oTesseractRequest req, Bitmap bitmap)
    {
        using (TesseractProcessor processor = new TesseractProcessor())
        {
            processor.InitForAnalysePage();
            using (GreyImage greyImage = GreyImage.FromImage(bitmap))
            {
                //greyImage.Save(ImageFormat.Bmp, outFile2);

                ImageThresholder thresholder = new AdaptiveThresholder();
                using (BinaryImage binImage = thresholder.Threshold(greyImage))
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
                                doc = processor.AnalyseLayoutBinaryImage(
                                    binImage.BinaryData, greyImage.Width, greyImage.Height);
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
                                req.output_format = "left_top_right_bottom";
                                req.output_text = string.Join("|", bs.ToArray());
                                req.output_count = bs.Count;
                                req.ok = 1;
                            }
                            break;
                    }
                }
            }
        }

        return req;
    }

    static void __executeBackground(byte[] buf)
    {
        oTesseractRequest r = null;
        string guid = Encoding.ASCII.GetString(buf);
        var redis = new RedisBase(new RedisSetting(REDIS_TYPE.ONLY_READ, __PORT_WRITE));
        try
        {
            string json = redis.HGET("_OCR_REQUEST", guid);
            r = JsonConvert.DeserializeObject<oTesseractRequest>(json);
            Bitmap bitmap = redis.HGET_BITMAP(r.redis_key, r.redis_field);
            if (bitmap != null) 
                r = __ocrExecute(r, bitmap);
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
            redis.HSET("_OCR_REQ_LOG", r.requestId, r.ok == -1 ? "-1" : r.output_count.ToString());
            redis.PUBLISH("__TESSERACT_OUT", r.requestId);
        }
    }

    static void __startApp()
    {
        m_subcriber = new RedisBase(new RedisSetting(REDIS_TYPE.ONLY_SUBCRIBE, __PORT_READ));
        m_subcriber.PSUBSCRIBE("__TESSERACT_IN");
        var bs = new List<byte>();
        while (__running)
        {
            if (!m_subcriber.m_stream.DataAvailable)
            {
                if (bs.Count > 0)
                {
                    var buf = m_subcriber.__getBodyPublish("__TESSERACT_IN", bs.ToArray());
                    bs.Clear();
                    new Thread(new ParameterizedThreadStart((o) => __executeBackground((byte[])o))).Start(buf);
                }
                Thread.Sleep(100);
                continue;
            }
            byte b = (byte)m_subcriber.m_stream.ReadByte();
            bs.Add(b);
        }
    }
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

