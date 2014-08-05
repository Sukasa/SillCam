﻿using System;
using System.Drawing;
using System.Threading;
using IvyBus;
using System.Configuration;

using RaspberryCam;

namespace SillCam
{
    class Program
    {
        private static int HistorySize = 10;
        private static int SavePeriod = 20;
        private static int RollingPtr = 0;
        private static int SaveTimer = 0;
        private static string OutputFormat = "/home/camera/img/capture{0}.jpg";
        private static string Domain = "192.168.1:2010";
        private static Picture[] Snapshots;
        private static Cameras Camera;
        private static Object LockObj = new Object();
        private static Ivy Ivy;

        private static Int64 TimestampStart;

        static void Main(string[] args)
        {

            Domain = Properties.Settings.Default.Domain;
            OutputFormat = Properties.Settings.Default.OutputFormat;
            HistorySize = Properties.Settings.Default.HistorySize;
            SavePeriod = Properties.Settings.Default.SavePeriod;

            Snapshots = new Picture[HistorySize];

            Ivy = new Ivy();
            Ivy.AppName = "Sill Camera Controller";
            Ivy.BindMsg("TOKEN_.*", SaveHistory, null);
            Ivy.BindMsg("SILLCAM_CAPTURE", SaveSinglePicture, null);
            Ivy.DebugProtocol = true;
            Ivy.Start(Domain);
            

            Camera = Cameras.DeclareDevice().Named("Camera 1").WithDevicePath("/dev/video0").Memorize();
            Camera.Get("Camera 1").StartVideoStreaming(new PictureSize(640, 480), 1);

            while (true)
            {
                Thread.Sleep(1000);

                lock (LockObj)
                {
                    TakePicture();
                    if (SaveTimer > 0)
                    {
                        Console.WriteLine("Rolling Save");
                        SaveTimer--;
                        SavePicture(RollingPtr);
                        if (SaveTimer == 0)
                        {
                            Ivy.SendMsg("SILLCAM_ROLLINGCOMPLETE:{0}:{1}", TimestampStart, Timestamp());
                        }
                    }
                    RollingPtr = (++RollingPtr) % HistorySize;
                }
            }
        }

        static void SaveSinglePicture(object sender, IvyMessageEventArgs e)
        {
            Console.WriteLine("Saving single picture");
            lock (LockObj)
            {
                int Index = (HistorySize - 1 + RollingPtr) % HistorySize;

                Int64 i = SavePicture(Index);
                Ivy.SendMsg("SILLCAM_PICTURE:" + string.Format(OutputFormat, i));
            }
        }

        static void SaveHistory(object sender, IvyMessageEventArgs e)
        {
            Console.WriteLine("Saving history");
            int Count = 0;
            lock (LockObj)
            {
                if (SaveTimer == 0)
                    TimestampStart = Timestamp();
                SaveTimer = SavePeriod;
                for (int SaveIndex = RollingPtr; Count < HistorySize; SaveIndex = ++SaveIndex % HistorySize, Count++)
                {
                    SavePicture(SaveIndex);
                }
            }
        }

        static Int64 SavePicture(int Index)
        {
            Picture Snapshot = Snapshots[Index];
            if (Snapshot.PictureData == null)
                return -1;
            ImageConverter ic = new ImageConverter();
            Image img = (Image)ic.ConvertFrom(Snapshot.PictureData);
            String Filename = string.Format(OutputFormat, Timestamp(Snapshot.Timestamp));
            img.Save(Filename);
            Console.WriteLine("Wrote Image to file: " + Filename);

            return Timestamp();
        }

        static void TakePicture()
        {
            Picture Snapshot;
            Snapshot.PictureData = Camera.Get("Camera 1").GetVideoFrame();
            Snapshot.Timestamp = DateTime.Now;
            Snapshots[RollingPtr] = Snapshot;
        }

        private struct Picture
        {
            public byte[] PictureData;
            public DateTime Timestamp;
        }

        public static Int64 Timestamp()
        {
            return Timestamp(DateTime.Now);
        }


        public static Int64 Timestamp(DateTime dateTime)
        {
            return (Int64)(dateTime - new DateTime(1970, 1, 1).ToLocalTime()).TotalSeconds;
        }
    }
}
