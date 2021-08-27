using System;
using HunterPie.Core;
using HunterPie.Core.Input;
using HunterPie.Core.Events;
using System.IO;
using System.Reflection;
using HunterPie.Memory;
using System.Threading;
using System.Collections.Generic;
using System.Text;
using HunterPie.Core.Definitions;
using HunterPie.Core.Native;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using HunterPie.Native.Connection.Packets;
using HunterPie.Native.Connection;
using System.Net;

namespace HunterPie.Plugins.MonsterActionController
{
    public class MonsterActionController : IPlugin
    {
        // This is your plugin name
        public string Name { get; set; } = "MonsterActionController";

        // This is your plugin description, try to be as direct as possible on what your plugin does
        public string Description { get; set; } = "A plugin to enable you to control the action of monsters";

        // This is our game context, you'll use it to track in-game information and hook events
        public Game Context { get; set; }

        private long MonsterAddress { get; set; }
        private Thread thread;
        private Thread lockMountThread;
        private Thread lockHealthThread;
        private Monster targetMonster;

        private int selectedID = 0;
        private int maxID;

        bool is_debugging = false;
        bool is_health_locked = false;
        bool is_beep = false;
        bool is_monster_selected = false;

        public ChatInChinese chatInChinese = new ChatInChinese();

        readonly List<string> monsterNameList = new List<string>() { };
        readonly Dictionary<char, List<int>> cmdValues = new Dictionary<char, List<int>>() {
            { 'J', new List< int>(){} },
            { 'K', new List<int>() {} },
            { 'L', new List<int>() {} },
            { 'U', new List<int>() {} },
            { 'O', new List<int>() {} },
             { 'B', new List<int>() {} } ,
              { 'N', new List<int>() {} },
              { 'M', new List<int>() {} },
              { 'Z', new List<int>() {} },
              { 'V', new List<int>() {} },
              { 'P', new List<int>() {} } };
        readonly Dictionary<char, List<string>> cmdExplanations = new Dictionary<char, List<string>>() {
            { 'J', new List< string>(){} },
            { 'K', new List<string>() {} },
            { 'L', new List<string>() {} },
            { 'U', new List<string>() {} },
            { 'O', new List<string>() {} },
             { 'B', new List<string>() {} } ,
              { 'N', new List<string>() {} },
              { 'M', new List<string>() {} },
              { 'Z', new List<string>() {} },
              { 'V', new List<string>() {} },
              { 'P', new List<string>() {} } };

        #region Load Config: actions.csv
        public static List<String[]> ReadCSV(string filePathName)
        {
            List<String[]> ls = new List<String[]>();
            StreamReader fileReader = new StreamReader(filePathName, Encoding.Default);
            string strLine = "";
            while (strLine != null)
            {
                strLine = fileReader.ReadLine();
                if (strLine != null && strLine.Length > 0)
                {
                    ls.Add(strLine.Split(','));
                }
            }
            fileReader.Close();
            return ls;
        }

        private void LoadConfig()
        {
            if (!File.Exists("Modules\\MonsterActionController\\actions.csv"))
            {
                this.Error("找不到HunterPie\\Modules\\MonsterActionController\\actions.csv配置文件！");
                this.Error("HunterPie\\Modules\\MonsterActionController\\actions.csv NOT FOUND!");
            }

            List<string[]> data = ReadCSV("Modules\\MonsterActionController\\actions.csv");
            int cnt = (data[0].Length - 1) / 2;
            for (int i = 0; i < cnt; ++i)
                monsterNameList.Add(data[0][2 * i + 1]);
            for (int i = 1; i <= 11; ++i)
            {
                for (int j = 0; j < cnt; ++j)
                {
                    cmdValues[data[i][0][0]].Add(int.Parse(data[i][2 * j + 1]));
                    cmdExplanations[data[i][0][0]].Add(data[i][2 * j + 2]);
                }
            }
        }
        #endregion

        #region HunterPie API methods
        public void Initialize(Game context)
        {
            chatInChinese.Init();

            LoadConfig();
            Context = context;
            MonsterAddress = 0;
            maxID = monsterNameList.Count;

            CreateHotkeys();
            HookEvents();

        }

        public void Unload()
        {
            RemoveHotkeys();

            UnhookEvents();
        }

        readonly int[] hotkeyIds = new int[18];
        public void CreateHotkeys()
        {
            hotkeyIds[0] = Hotkey.Register("Alt+J", () => { HotkeyCallback('J'); });
            hotkeyIds[1] = Hotkey.Register("Alt+K", () => { HotkeyCallback('K'); });
            hotkeyIds[2] = Hotkey.Register("Alt+L", () => { HotkeyCallback('L'); });
            hotkeyIds[3] = Hotkey.Register("Alt+U", () => { HotkeyCallback('U'); });
            hotkeyIds[4] = Hotkey.Register("Alt+I", () => { /*key conflict*/ });
            hotkeyIds[5] = Hotkey.Register("Alt+O", () => { HotkeyCallback('O'); });
            hotkeyIds[9] = Hotkey.Register("Alt+B", () => { HotkeyCallback('B'); });
            hotkeyIds[10] = Hotkey.Register("Alt+N", () => { HotkeyCallback('N'); });
            hotkeyIds[11] = Hotkey.Register("Alt+M", () => { HotkeyCallback('M'); });
            hotkeyIds[12] = Hotkey.Register("Alt+Z", () => { HotkeyCallback('Z'); });
            hotkeyIds[13] = Hotkey.Register("Alt+V", () => { HotkeyCallback('V'); });
            hotkeyIds[14] = Hotkey.Register("Alt+P", () => { HotkeyCallback('P'); });

            hotkeyIds[6] = Hotkey.Register("Alt+D", () =>
            {
                DebugTest();
                is_debugging = !is_debugging;
                this.Log($"is_debugging: {is_debugging}");
                string tmp = is_debugging ? "开启" : "关闭";
                _ = chatInChinese.SystemMessage($"<STYL MOJI_LIGHTBLUE_DEFAULT><ICON SLG_NEWS>DEBUG&SAVE模式</STYL>\n{tmp}", 0, 0, 0);
                if (!File.Exists("Actionlog.csv"))
                {
                    using (StreamWriter sw = new StreamWriter(path: "Actionlog.csv", false, Encoding.Default))
                    {
                        sw.WriteLine("Name,ActionID,ActionReferenceName,ActionName");
                    }
                }
            });

            hotkeyIds[7] = Hotkey.Register("Alt+T", () => {
                is_monster_selected = true;
                selectedID = (selectedID + 1) % maxID;
                this.Log($"切换到: {monsterNameList[selectedID]}");
                _ = chatInChinese.SystemMessage($"<STYL MOJI_LIGHTBLUE_DEFAULT><ICON SLG_NEWS>切换到怪物</STYL>\n{monsterNameList[selectedID]}", 0, 0, 1);
            });

            hotkeyIds[8] = Hotkey.Register("Alt+S", () => {
                StopThread();
                _ = chatInChinese.SystemMessage($"<STYL MOJI_LIGHTBLUE_DEFAULT><ICON SLG_NEWS>Alt+S</STYL>\n", 0, 0, 1);
            });

            hotkeyIds[15] = Hotkey.Register("Alt+E", () => { LockMount(); });

            hotkeyIds[16] = Hotkey.Register("Alt+1", () => { StopThread(true); });

            hotkeyIds[17] = Hotkey.Register("Alt+R", () => {/*
                is_beep = !is_beep;
                string tmp = is_beep ? "系统提示音" : "聊天栏提示";
                _ = ChatInChinese.SystemMessage(ANSI2UTF8($"<STYL MOJI_LIGHTBLUE_DEFAULT><ICON SLG_NEWS>切换提示模式</STYL>\n{tmp}"), 0, 0, 1);
            */
            });
        }
        public void RemoveHotkeys()
        {
            foreach (int id in hotkeyIds)
                Hotkey.Unregister(id);
        }
        private void HookEvents()
        {
            // We can access the Player, Monsters and World from Context
            Context.FirstMonster.OnActionChange += OnMonsterActionChangeCallBack;
            Context.SecondMonster.OnActionChange += OnMonsterActionChangeCallBack;
            Context.ThirdMonster.OnActionChange += OnMonsterActionChangeCallBack;
        }

        private void UnhookEvents()
        {
            // To unhook events, we just do the same thing but with a minus instead of a plus
            Context.FirstMonster.OnActionChange -= OnMonsterActionChangeCallBack;
            Context.SecondMonster.OnActionChange -= OnMonsterActionChangeCallBack;
            Context.ThirdMonster.OnActionChange -= OnMonsterActionChangeCallBack;
        }
        #endregion

        #region Update
        public static string HttpDownloadFile(string url, string path)
        {
            HttpWebRequest request = WebRequest.Create(url) as HttpWebRequest;
            HttpWebResponse response = request.GetResponse() as HttpWebResponse;
            Stream responseStream = response.GetResponseStream();
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
        private void UpdatePlugin()
        {
            try
            {
                HttpDownloadFile("https://cdn.jsdelivr.net/gh/WLLEGit/MHW-MonsterActionController@main/Monster%20Action%20Controller.dll", "Monster Action Controller.dll");
                this.Log("插件检查更新完成");
            }
            catch
            {
                this.Log("插件检查更新失败，检查网络设置");
            }
        }
        #endregion

        private void StopThread(bool stopAll = false)
        {
            if (thread != null)
                thread.Abort();
            if (stopAll)
            {
                if (lockHealthThread != null)
                    lockHealthThread.Abort();
                if (lockMountThread != null)
                    lockMountThread.Abort();
            }

        }

        public void HotkeyCallback(char cmd)
        {
            if (!is_monster_selected)
            {
                //considering that only one SystemMessage can be shown at a short interval
                _ = chatInChinese.SystemMessage($"<STYL MOJI_LIGHTBLUE_DEFAULT><ICON SLG_NEWS>尚未选择怪物</STYL>使用ALT+T切换", 0, 0, 1);
                _ = chatInChinese.Say("尚未选择怪物，使用ALT+T切换");
                return;
            }

            if (is_beep)
                System.Media.SystemSounds.Beep.Play();
            else
                _ = chatInChinese.Say($"Alt+{cmd}: {cmdExplanations[cmd][selectedID]}");

            LockStaminaAndHealth();
            StopThread();
            this.Log($"Alt+{cmd}: {cmdExplanations[cmd][selectedID]}");

            FieldInfo fieldInfo = typeof(Monster).GetField("monsterAddress", BindingFlags.Instance | BindingFlags.NonPublic);
            MonsterAddress = (long)fieldInfo.GetValue(Context.HuntedMonster);
            if (MonsterAddress == 0)
                return;
            long actionPointer = MonsterAddress + 0x61C8 + 0xB0;

            thread = new Thread(() =>
            {
                while (true)
                {
                    if (Kernel.Read<int>(actionPointer) != cmdValues[cmd][selectedID])
                        Kernel.Write<int>(actionPointer, cmdValues[cmd][selectedID]);
                }
            });
            thread.Start();
        }


        private Monster GetTargetMonster()
        {
            if (Context.HuntedMonster == null)
                return targetMonster;
            else
                return Context.HuntedMonster;
        }

        private void LockStaminaAndHealth()
        {
            if (is_health_locked)
                return;

            lockHealthThread = new Thread(() =>
            {
                this.Log("Stamina Locked");
                long address = Kernel.ReadMultilevelPtr(Address.GetAddress("BASE") + Address.GetAddress("EQUIPMENT_OFFSET"), Address.GetOffsets("PlayerBasicInformationOffsets"));
                //float[] health;


                while (true)
                {
                    float maxStamina = Kernel.Read<float>(address + 0x144);
                    float curStamina = Kernel.Read<float>(address + 0x13C);
                    if (maxStamina != curStamina)
                        Kernel.Write<float>(address + 0x13C, maxStamina - 10);

                    //health = Kernel.ReadStructure<float>(address + 0x60, 2);
                    //if(health[0] != health[1])
                    //    Kernel.Write<float>(address + 0x60 + 4, 99);
                    Thread.Sleep(200);
                }

            });
            lockHealthThread.Start();
            is_health_locked = true;
        }

        private void LockMount()
        {
            LockStaminaAndHealth();

            if (lockMountThread != null)
                lockMountThread.Abort();
            lockMountThread = new Thread(() =>
            {
                Ailment mountAil;
                mountAil = GetTargetMonster().Ailments[0];
                foreach (Ailment ailment in GetTargetMonster().Ailments)
                    if (ailment.Name == "骑乘" || ailment.Name == "Mount")
                        mountAil = ailment;

                long curBuildupAddr = mountAil.Address + sizeof(int) * 7 + sizeof(long) + sizeof(float) * 3;
                long maxBuildupAddr = mountAil.Address + sizeof(int) * 8 + sizeof(long) + sizeof(float) * 6;
                for (int _ = 0; _ < 1; ++_)   //once is OK
                {
                    float maxBuildup = Kernel.Read<float>(maxBuildupAddr);
                    float curBuildup = Kernel.Read<float>(curBuildupAddr);
                    if (curBuildup != maxBuildup)
                    {
                        Kernel.Write<float>(curBuildupAddr, maxBuildup);
                    }
                }
            });
            lockMountThread.Start();
        }
        private void OnMonsterActionChangeCallBack(object source, MonsterUpdateEventArgs args)
        {
            Monster tar = (Monster)source;
            targetMonster = tar;

            if (!is_debugging)
                return;
            using (StreamWriter sw = new StreamWriter("Actionlog.csv", append: true, encoding: Encoding.Default))
            {
                sw.WriteLine($"{tar.Name},{tar.ActionId},{tar.ActionReferenceName},{tar.ActionName}");
            }
        }

        private void DebugTest()
        {
            // _ = chatInChinese.Say("123中文测试abc");
        }

    }

    public class ChatInChinese
    {
        Client client = new Client();
        MethodInfo sendRawAsync;

        public void Init()
        {
            Type type = client.GetType();
            BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            sendRawAsync = type.GetMethod("SendRawAsync", flags);
        }
        public bool Say(string str)
        {
            byte[] message = new byte[256];
            BitConverter.GetBytes((int)OPCODE.SendChatMessage).CopyTo(message, 0);
            BitConverter.GetBytes((uint)1).CopyTo(message, 4);
            System.Text.Encoding.GetEncoding("UTF-8").GetBytes(str).CopyTo(message, 8);


            _ = sendRawAsync.Invoke(Client.Instance, new object[] { message });

            return true;
        }

        public bool SystemMessage(string str, float unk1, uint unk2, byte isPurple)
        {
            byte[] message = new byte[256];
            BitConverter.GetBytes((int)OPCODE.SendSystemMessage).CopyTo(message, 0);
            BitConverter.GetBytes((uint)1).CopyTo(message, 4);
            System.Text.Encoding.GetEncoding("UTF-8").GetBytes(str).CopyTo(message, 8);
            BitConverter.GetBytes((float)1).CopyTo(message, 246);
            BitConverter.GetBytes((uint)1).CopyTo(message, 250);
            BitConverter.GetBytes((byte)isPurple).CopyTo(message, 254);


            _ = sendRawAsync.Invoke(Client.Instance, new object[] { message });

            return true;
        }
    }
}
