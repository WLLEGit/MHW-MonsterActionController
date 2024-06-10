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
using System.Linq;
using HunterPie.Core.Settings;
using System.Timers;
using System.IO.Pipes;
using System.Net.Http;
using Newtonsoft.Json;

namespace HunterPie.Plugins.MonsterActionController
{
    public class MonsterActionController : IPlugin
    {
        // This is your plugin name
        public string Name { get; set; } = "MonsterActionController";

        // This is your plugin description, try to be as direct as possible on what your plugin does
        public string Description { get; set; } = "A plugin for controlling the action of monsters";

        // This is our game context, you'll use it to track in-game information and hook events
        public Game Context { get; set; }

        private long MonsterAddress { get; set; }
        private Thread lockActionThread;
        private Thread lockMountThread;
        private Thread lockHealthThread;
        private Thread actionTraceThread;

        private Monster targetMonster;

        private int selectedID = -1;

        private bool isDebugging = false;

        private bool isHealthLocked = false;

        private bool isMonsterSelected = false;

        private readonly DateTime launchTime = DateTime.Now;

        public ChatInChinese chatInChinese = new ChatInChinese();

        List<string> monsterNameList = new List<string>() { };
        Dictionary<string, List<ActionList>> cmdValues = new Dictionary<string, List<ActionList>>() { };
        Dictionary<string, List<string>> cmdExplanations = new Dictionary<string, List<string>>() { };

        private System.Timers.Timer notifyAliveTimer = new System.Timers.Timer(10000);

        private Settings settings;

        #region Load Config: actions.csv
        public static List<String[]> ReadCSV(string filePathName)
        {
            List<String[]> ls = new List<String[]>();
            using (FileStream fileStream = new FileStream(filePathName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader fileReader = new StreamReader(fileStream, Encoding.Default))
            {
                string strLine = "";
                while (strLine != null)
                {
                    strLine = fileReader.ReadLine();
                    if (strLine != null && strLine.Length > 0)
                    {
                        ls.Add(strLine.Split(','));
                    }
                }
            }

            return ls;
        }

        private void LoadSettings()
        {
            Task<Settings> getSettingsTask = this.LoadJson<Settings>("settings.json");
            getSettingsTask.Wait();
            settings = getSettingsTask.Result;

            if (!CheckNotNullRecursive(settings))
                this.Error("settings.json不存在或格式错误");
        }

        private void LoadConfig()
        {
            if (!File.Exists("Modules\\MonsterActionController\\actions.csv"))
            {
                this.Error("找不到HunterPie\\Modules\\MonsterActionController\\actions.csv配置文件！");
            }

            List<string[]> data = ReadCSV("Modules\\MonsterActionController\\actions.csv");
            int monsterCnt = (data[0].Length - 1) / 2;
            int hotkeyCnt = (data.Count - 1);

            for (int i = 0; i < monsterCnt; ++i)
                monsterNameList.Add(data[0][2 * i + 1]);
            for (int i = 1; i <= hotkeyCnt; ++i)
            {
                string hotkey = data[i][0];
                if (hotkey.Length == 1)
                    hotkey = $"Alt+{hotkey}";   // Backward compatibility
                cmdValues[hotkey] = new List<ActionList>();
                cmdExplanations[hotkey] = new List<string>();
                for (int j = 0; j < monsterCnt; ++j)
                {
                    cmdValues[hotkey].Add(new ActionList(data[i][2 * j + 1]));
                    cmdExplanations[hotkey].Add(data[i][2 * j + 2]);
                }
            }
        }
        #endregion

        #region HunterPie API methods
        public void Initialize(Game context)
        {
            chatInChinese.Init();

            LoadSettings();
            LoadConfig();
            Context = context;
            MonsterAddress = 0;
            notifyAliveTimer.Elapsed += (s, e) => chatInChinese.Say("MonsterActionController 运行中");
            notifyAliveTimer.AutoReset = false;

            CreateHotkeys();
            HookEvents();

            CheckPluginUpdate();

            this.Log("MonsterActionController 初始化完成");
        }

        public void Unload()
        {
            RemoveHotkeys();

            UnhookEvents();
        }

        private int[] hotkeyIds = new int[128];
        private int hotkeyCnt = 0;

        private void LocalHotkeyRegister(string keys, Action callback)
        {
            int hotkeyId = Hotkey.Register(keys, callback);
            if (hotkeyId == -1)
                this.Error($"快捷键 {keys} 注册失败");
            hotkeyIds[hotkeyCnt++] = hotkeyId;
        }

        public void CreateHotkeys()
        {
            hotkeyCnt = 0;

            LocalHotkeyRegister(settings.builtinHotkeys.toggleDebug, () =>
            {
                isDebugging = !isDebugging;
                this.Log($"is_debugging: {isDebugging}");
                string tmp = isDebugging ? "开启" : "关闭";
                _ = chatInChinese.Say($"调试模式 {tmp}");
                if (!File.Exists("Actionlog.csv"))
                {
                    using (StreamWriter sw = new StreamWriter(path: "Actionlog.csv", false, Encoding.Default))
                    {
                        sw.WriteLine("Timestamp(100ns),Name,ActionID,ActionReferenceName,ActionName");
                    }
                }

                if (isDebugging && (actionTraceThread == null || !actionTraceThread.IsAlive))
                {
                    actionTraceThread = new Thread(() =>
                    {
                        using (FileStream fileStream = new FileStream("Actionlog.csv", FileMode.Append, FileAccess.Write, FileShare.Read))
                        using (StreamWriter sw = new StreamWriter(fileStream, Encoding.Default))
                        {
                            FieldInfo fieldInfo = typeof(Monster).GetField("monsterAddress", BindingFlags.Instance | BindingFlags.NonPublic);
                            int prevId = -1;
                            while (isDebugging)
                            {
                                if (Context.HuntedMonster == null) continue;
                                MonsterAddress = (long)fieldInfo.GetValue(Context.HuntedMonster);
                                if (MonsterAddress == 0)
                                    continue;

                                long actionPointer = MonsterAddress + 0x61C8;
                                int actionId = Kernel.Read<int>(actionPointer + 0xB0);

                                if (actionId != prevId)
                                {
                                    prevId = actionId;

                                    actionPointer = Kernel.Read<long>(actionPointer + (2 * 8) + 0x68);
                                    actionPointer = Kernel.Read<long>(actionPointer + actionId * 8);
                                    actionPointer = Kernel.Read<long>(actionPointer);
                                    actionPointer = Kernel.Read<long>(actionPointer + 0x20);
                                    uint actionOffset = Kernel.Read<uint>(actionPointer + 3);
                                    long actionRef = actionPointer + actionOffset + 7;
                                    actionRef = Kernel.Read<long>(actionRef + 8);
                                    string actionRefString = Kernel.ReadString(actionRef, 64);
                                    string ActionName = Monster.ParseActionString(actionRefString);
                                    sw.WriteLine($"{DateTime.Now.Ticks - launchTime.Ticks},{Context.HuntedMonster?.Name},{actionId},{actionRefString},{ActionName}");
                                    sw.Flush();
                                }
                            }
                        }
                    });
                    actionTraceThread.Start();
                }

            });

            LocalHotkeyRegister(settings.builtinHotkeys.toggleMonster, () =>
            {
                isMonsterSelected = true;
                selectedID = (selectedID + 1) % monsterNameList.Count;
                this.Log($"切换到: {monsterNameList[selectedID]}");
                _ = chatInChinese.Say($"切换到: {monsterNameList[selectedID]}");
            });

            LocalHotkeyRegister(settings.builtinHotkeys.stopControl, () =>
            {
                if (lockActionThread != null)
                    lockActionThread.Abort();
                this.Log("停止锁定");
                if (isDebugging)
                    _ = chatInChinese.Say($"停止锁定");
                else
                    notifyAliveTimer.Enabled = true;
            });

            LocalHotkeyRegister(settings.builtinHotkeys.forceMount, () => { LockMount(); });

            foreach (string hotkey in cmdValues.Keys)
            {
                LocalHotkeyRegister(hotkey, () => HotkeyCallback(hotkey));
            }
        }
        public void RemoveHotkeys()
        {
            for (int i = 0; i < hotkeyCnt; i++)
                Hotkey.Unregister(hotkeyIds[i]);
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


        public void HotkeyCallback(string hotkey)
        {
            if (!isMonsterSelected)
            {
                _ = chatInChinese.Say("尚未选择怪物，使用ALT+T切换");
                return;
            }

            if (isDebugging)
                _ = chatInChinese.Say($"{hotkey}: {cmdExplanations[hotkey][selectedID]}");
            else
                notifyAliveTimer.Enabled = true;

            this.Log($"{hotkey}: {cmdExplanations[hotkey][selectedID]}");

            FieldInfo fieldInfo = typeof(Monster).GetField("monsterAddress", BindingFlags.Instance | BindingFlags.NonPublic);
            MonsterAddress = (long)fieldInfo.GetValue(Context.HuntedMonster);
            if (MonsterAddress == 0)
                return;
            long actionPointer = MonsterAddress + 0x61C8 + 0xB0;

            lockActionThread?.Abort();
            lockActionThread = new Thread(() =>
            {
                ActionList actionGroup = cmdValues[hotkey][selectedID];

                do
                {
                    int initAct = Kernel.Read<int>(actionPointer), curAct = initAct;
                    for (int i = 0; i < actionGroup.actions.Count; ++i)
                    {
                        ActionList.ActionConfig expectAct = actionGroup.actions[i];
                        ActionList.ActionConfig nextAct = null;
                        if (i + 1 == actionGroup.actions.Count && actionGroup.isRepeat)
                            nextAct = actionGroup.actions[0];
                        else if (i + 1 < actionGroup.actions.Count)
                            nextAct = actionGroup.actions[i + 1];


                        // 等待第一次改变
                        while (curAct == initAct)
                            curAct = Kernel.Read<int>(actionPointer);
                        if (!expectAct.idCandidates.Contains(curAct))
                            Kernel.Write<int>(actionPointer, expectAct.idCandidates[0]);

                        DateTime lockEnd = DateTime.Now.AddSeconds(expectAct.duration);

                        while (DateTime.Now < lockEnd)
                        {
                            curAct = Kernel.Read<int>(actionPointer);
                            if (!expectAct.idCandidates.Contains(curAct) && (nextAct == null || !nextAct.idCandidates.Contains(curAct)))
                            {
                                Kernel.Write<int>(actionPointer, expectAct.idCandidates[0]);
                                lockEnd = DateTime.Now.AddSeconds(expectAct.duration);
                            }
                        }

                        initAct = curAct;
                    }
                } while (actionGroup.isRepeat);
            });
            lockActionThread.Start();
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
            if (isHealthLocked)
                return;

            lockHealthThread = new Thread(() =>
            {
                this.Log("Stamina Locked");
                long address = Kernel.ReadMultilevelPtr(Address.GetAddress("BASE") + Address.GetAddress("EQUIPMENT_OFFSET"), Address.GetOffsets("PlayerBasicInformationOffsets"));
                //float[] health;


                while (true)
                {
                    float maxStamina = Kernel.Read<float>(address + 0x130);
                    float curStamina = Kernel.Read<float>(address + 0x12C);
                    if (maxStamina != curStamina)
                        Kernel.Write<float>(address + 0x12C, maxStamina);

                    //health = Kernel.ReadStructure<float>(address + 0x60, 2);
                    //if(health[0] != health[1])
                    //    Kernel.Write<float>(address + 0x60 + 4, 99);
                    Thread.Sleep(200);
                }

            });
            lockHealthThread.Start();
            isHealthLocked = true;
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
        }


        private async void CheckPluginUpdate()
        {
            string releaseUrl = "https://github.com/WLLEGit/MHW-MonsterActionController/releases";
            string apiUrl = "https://api.github.com/repos/WLLEGit/MHW-MonsterActionController/releases/latest";
            try
            {
                using (HttpClient httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                    httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36 Edg/119.0.0.0");

                    HttpResponseMessage response = await httpClient.GetAsync(apiUrl);
                    response.EnsureSuccessStatusCode();
                    string resp = await response.Content.ReadAsStringAsync();
                    GithubLatestReleaseApiResp json = JsonConvert.DeserializeObject<GithubLatestReleaseApiResp>(resp);
                    string tag = json.tag_name;
                    PluginInformation pluginInfo = await this.LoadJson<PluginInformation>("module.json");
                    string curTag = $"v{pluginInfo?.Version}";
                    if (tag != curTag)
                    {
                        this.Log($"插件非最新版，当前{curTag}，最新{tag}");
                        this.Log(releaseUrl);
                    }
                }
            }
            catch (Exception)
            {
                this.Log("无法获取Github Release信息，请手动检查插件是否为最新版。");
                this.Log(releaseUrl);
            }
        }

        private bool CheckNotNullRecursive<T>(T obj)
        {
            foreach (PropertyInfo pi in obj.GetType().GetProperties())
            {
                object property = pi.GetValue(obj);
                if (property == null)
                    return false;
                if (!CheckNotNullRecursive(property))
                    return false;
            }
            return true;
        }

        public class Settings
        {
            public class BuiltinHotkeys
            {
                public string toggleDebug;
                public string toggleMonster;
                public string stopControl;
                public string forceMount;
            }

            public BuiltinHotkeys builtinHotkeys;
        }

        public class GithubLatestReleaseApiResp
        {
            public string tag_name;
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
    public class ActionList
    {
        public class ActionConfig
        {
            public List<int> idCandidates;
            public float duration;

            public ActionConfig(string config)
            {
                idCandidates = new List<int>();
                duration = 0;

                List<string> actionCandidates = config.Split('/').ToList();
                foreach (string actionCandidate in actionCandidates)
                {
                    if (actionCandidate.Contains("@"))
                    {
                        List<string> strings = actionCandidate.Split('@').ToList();
                        idCandidates.Add(int.Parse(strings[0]));
                        if (duration == 0)
                            duration = float.Parse(strings[1]);
                    }
                    else
                    {
                        idCandidates.Add(int.Parse(actionCandidate));
                    }
                }

                if (duration == 0)
                    duration = 0.2f;
            }
        }
        public readonly bool isRepeat = false;
        public readonly List<ActionConfig> actions = new List<ActionConfig>();
        public ActionList(string config)
        {
            if (config.StartsWith("REPEAT:"))
            {
                isRepeat = true;
                config = config.Substring(7);
            }

            if (int.TryParse(config, out _))
            {
                actions.Add(new ActionConfig(config));
                actions[0].duration = 86400;
            }
            else
            {
                List<string> actionList = config.Split('-').ToList();
                foreach (string actionConfig in actionList)
                {
                    actions.Add(new ActionConfig(actionConfig));
                }
            }
        }
    }

}
