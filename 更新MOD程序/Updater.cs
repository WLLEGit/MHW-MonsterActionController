using System;
using System.IO;
using System.Collections.Generic;
using System.Net;

namespace test
{
    class Updater
    {
        static void Main(string[] args)
        {
            Console.WriteLine("下载中……");
            HttpDownloadFile("https://cdn.jsdelivr.net/gh/WLLEGit/MHW-MonsterActionController@main/actions.csv", "actions.csv");
            HttpDownloadFile("https://cdn.jsdelivr.net/gh/WLLEGit/MHW-MonsterActionController@main/Monster%20Action%20Controller.dll", "Monster Action Controller.dll");
            Console.WriteLine("下载完成");
        }

        public static string HttpDownloadFile(string url, string path)
        {
            // 设置参数
            HttpWebRequest request = WebRequest.Create(url) as HttpWebRequest;
            //发送请求并获取相应回应数据
            HttpWebResponse response = request.GetResponse() as HttpWebResponse;
            //直到request.GetResponse()程序才开始向目标网页发送Post请求
            Stream responseStream = response.GetResponseStream();
            //创建本地文件写入流
            Stream stream = new FileStream(path, FileMode.Create);
            byte[] bArr = new byte[1024];
            int size = responseStream.Read(bArr, 0, (int)bArr.Length);
            while (size > 0)
            {
                stream.Write(bArr, 0, size);
                size = responseStream.Read(bArr, 0, (int)bArr.Length);
            }
            stream.Close();
            responseStream.Close();
            return path;
        }
    }
}
