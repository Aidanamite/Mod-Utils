using HarmonyLib;
using UnityEngine;
using System;
using Object = UnityEngine.Object;
using Debug = UnityEngine.Debug;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine.UI;
using HMLLibrary;
using System.Linq;
using Steamworks;
using System.IO;
using System.Runtime.Serialization;
using Sirenix.Serialization;


namespace ModUtils
{
    public class Main : Mod
    {
        public Harmony harmony;
        public static List<ModHandler> loaded;
        public static Dictionary<Mod, ModHandler> pairs;
        public static Main instance;
        public void Start()
        {
            instance = this;
            loaded = new List<ModHandler>();
            pairs = new Dictionary<Mod, ModHandler>();
            (harmony = new Harmony("com.aidanamite.ModUtils")).PatchAll();
            foreach (var m in Resources.FindObjectsOfTypeAll<Mod>())
                modLoaded(m);
            InsertOptions();
            Log("Mod has been loaded!");
        }

        public void OnModUnload()
        {
            RemoveOptions();
            harmony.UnpatchAll(harmony.Id);
            Log("Mod has been unloaded!");
        }
        public override void WorldEvent_WorldUnloaded() => RemoveOptions();

        public static void modLoaded(Mod mod)
        {
            if (!mod)
                return;
            try
            {
                ModHandler handler = mod;
                foreach (var mH in loaded)
                {
                    if (handler.ModSlug == mH.ModSlug)
                        Debug.LogWarning($"Mod slug duplicate found. {handler.parent.GetModInfo().name} and {mH.parent.GetModInfo().name} were both found to have the mod slug \"{handler.ModSlug}\"");
                    handler.Load(mH.parent);
                    mH.Load(mod);
                }
                loaded.Add(handler);
                pairs.Add(mod, handler);
            } catch (Exception e)
            {
                Debug.LogError(e);
            }
        }
        public static void modUnloaded(Mod mod)
        {
            var remove = loaded.FindAll(x => !x.parent || x.parent == mod);
            var removed = new List<NetworkChannel>();
            foreach (var mH in remove) {
                removed.AddRangeUniqueOnly(mH.channels);
                loaded.Remove(mH);
                pairs.Remove(mH.parent);
            }
            foreach (var mH in loaded)
            {
                mH.Unload(mod);
                removed.RemoveAll(x => mH.channels?.Contains(x)??false);
            }
            ModHandler.allChannels.RemoveAll(x => removed.Contains(x));
        }

        public static void RecheckChannel(NetworkChannel channel)
        {
            foreach (var mH in loaded)
                if (mH.channels.Contains(channel))
                    return;
            ModHandler.allChannels.Remove(channel);
        }
        public static void RecheckChannels(IEnumerable<NetworkChannel> channels)
        {
            var r = channels.ToList();
            foreach (var mH in loaded)
                foreach (var c in mH.channels)
                    r.Remove(c);
            foreach (var c in r)
                ModHandler.allChannels.Remove(c);
        }

        public void Update()
        {
            foreach (var channel in ModHandler.allChannels)
            {
                if (channel == NetworkChannel.Channel_Session || channel == NetworkChannel.Channel_Game)
                    continue;
                while (SteamNetworking.IsP2PPacketAvailable(out var byteCount, (int)channel))
                {
                    byte[] bytes = new byte[byteCount];
                    if (SteamNetworking.ReadP2PPacket(bytes, byteCount, out _, out var steamId, (int)channel))
                    {
                        
                        var packet = SerializationUtility.DeserializeValue<Packet>(bytes, DataFormat.Binary, null);
                        foreach (var m in (packet as Packet_Multiple)?.messages ?? new[] { (packet as Packet_Single)?.message is Raft_Network.Message_FragmentedPacket fragment ? DeserializeFragmentedPacket(fragment)?.message : (packet as Packet_Single)?.message })
                            if (m != null)
                                foreach (var mH in loaded)
                                    if (mH.channels != null && mH.channels.Contains(channel))
                                        try
                                        {
                                            if (mH.ProcessMessage(steamId, channel, m))
                                                break;
                                        }
                                        catch (Exception e)
                                        {
                                            Debug.LogError($"[{mH.parent.modlistEntry.jsonmodinfo.name}]: An error occured while processing a network message\n{e}");
                                        }
                    }
                }
            }
        }

        public static Packet_Single DeserializeFragmentedPacket(Raft_Network.Message_FragmentedPacket fragment) => Traverse.Create(ComponentManager<Raft_Network>.Value).Method("DeserializeFragmentedPacket", fragment).GetValue() as Packet_Single;
        
        public static Mod GetMod(Type type)
        {
            foreach (var m in loaded)
                if (m.parent.GetType() == type)
                    return m.parent;
            return null;
        }
        public static ModHandler GetHandler(Mod mod)
        {
            if (pairs.TryGetValue(mod, out var h))
                return h;
            return null;
        }
        List<GameObject> addedObjects = new List<GameObject>();
        List<group> groupsV = new List<group>();
        List<group> groupsH = new List<group>();
        public void InsertOptions()
        {
            if (addedObjects.Any(x => !x))
                RemoveOptions();
            else if (addedObjects.Count != 0)
                return;
            var helper = ComponentManager<CanvasHelper>.Value;
            if (!helper) return;
            var menu = helper.GetMenu(MenuType.BuildMenu);
            if (menu == null || menu.menuObjects.Count == 0) return;
            var addToMenu = new List<(Item_Base, Item_Base, bool)>();
            foreach (var h in pairs.Values)
            {
                var adds = h.GetBuildMenuItems();
                if (adds != null)
                    addToMenu.AddRange(adds);
            }
            if (addToMenu.Count == 0) return;
            SortItems(addToMenu);
            foreach (var main in menu.menuObjects[0].GetComponentsInChildren<BuildMenuItem_SelectMainCategory>(true))
            {
                groupsH.Insert(0, new group());
                foreach (Transform hC in Traverse.Create(main).Field("horizontalParent").GetValue<GameObject>().transform)
                {
                    if (hC.name == "BrownBackground")
                    {
                        groupsH[0].background = hC.GetComponent<RectTransform>();
                        continue;
                    }
                    var b = hC.GetComponentInChildren<BuildMenuItem_SelectBlock>(true);
                    var c = hC.GetComponentInChildren<BuildMenuItem_SelectSubCategory>(true);
                    if (b || c)
                    {
                        if (!addedObjects.Contains(hC.gameObject))
                        {
                            groupsH[0].objects++;
                            groupsH[0].baseObjects++;
                        }
                        if (c)
                        {
                            var i = c.GetComponentsInChildren<BuildMenuItem_SelectBlock>(true).Cast(x => Traverse.Create(x).Field("buildableItem").GetValue<Item_Base>());
                            if (i.Count == 0) continue;
                            foreach (var item in addToMenu)
                                if (item.Item3 && i.Exists(x => x.UniqueIndex == item.Item1.UniqueIndex))
                                {
                                    var g = Instantiate(hC, hC.parent, false);
                                    var nc = g.GetComponentInChildren<BuildMenuItem_SelectSubCategory>(true);
                                    addedObjects.Add(g.gameObject);
                                    Traverse.Create(nc).Field("buildableItem").SetValue(item.Item2);
                                    Traverse.Create(nc).Method("RefreshIcon").GetValue();
                                    groupsH[0].objects++;
                                    var inPar = Traverse.Create(nc).Field("verticalParent").GetValue<GameObject>().transform;
                                    var flag = false;
                                    var back = inPar.Find("BrownBackground")?.GetComponent<RectTransform>();
                                    var count = 0;
                                    for (var j = inPar.childCount - 1; j >= 0; j--)
                                        if (inPar.GetChild(j).GetComponentInChildren<BuildMenuItem_SelectBlock>(true))
                                        {
                                            if (flag)
                                                DestroyImmediate(inPar.GetChild(j).gameObject);
                                            else
                                            {
                                                flag = true;
                                                var s = inPar.GetChild(j).GetComponentInChildren<BuildMenuItem_SelectBlock>(true);
                                                Traverse.Create(s).Field("buildableItem").SetValue(item.Item2);
                                                Traverse.Create(s).Method("RefreshIcon").GetValue();
                                            }
                                            count++;
                                        }
                                    if (back && count > 0)
                                        back.offsetMin += new Vector2(0, (back.offsetMax.y - back.offsetMin.y) / count * (count - 1));
                                }
                        } else
                        {
                            var i = Traverse.Create(b).Field("buildableItem").GetValue<Item_Base>();
                            if (!i) continue;
                            foreach (var item in addToMenu)
                                if (item.Item1.UniqueIndex == i.UniqueIndex && item.Item3)
                                {
                                    var g = Instantiate(hC, hC.parent, false);
                                    var nb = g.GetComponentInChildren<BuildMenuItem_SelectBlock>(true);
                                    addedObjects.Add(g.gameObject);
                                    Traverse.Create(nb).Field("buildableItem").SetValue(item.Item2);
                                    Traverse.Create(nb).Method("RefreshIcon").GetValue();
                                    groupsH[0].objects++;
                                }
                        }
                    }
                }
            }
            foreach (var main in menu.menuObjects[0].GetComponentsInChildren<BuildMenuItem_SelectSubCategory>(true))
            {
                groupsV.Insert(0, new group());
                foreach (Transform vC in Traverse.Create(main).Field("verticalParent").GetValue<GameObject>().transform)
                {
                    if (vC.name == "BrownBackground")
                    {
                        groupsV[0].background = vC.GetComponent<RectTransform>();
                        continue;
                    }
                    var b = vC.GetComponentInChildren<BuildMenuItem_SelectBlock>(true);
                    if (b)
                    {
                        if (!addedObjects.Contains(vC.gameObject))
                        {
                            groupsV[0].objects++;
                            groupsV[0].baseObjects++;
                        }
                        var i = Traverse.Create(b).Field("buildableItem").GetValue<Item_Base>();
                        if (!i) continue;
                        foreach (var item in addToMenu)
                            if (item.Item1.UniqueIndex == i.UniqueIndex && !item.Item3)
                            {
                                var g = Instantiate(vC, vC.parent, false);
                                var nb = g.GetComponentInChildren<BuildMenuItem_SelectBlock>(true);
                                addedObjects.Add(g.gameObject);
                                Traverse.Create(nb).Field("buildableItem").SetValue(item.Item2);
                                Traverse.Create(nb).Method("RefreshIcon").GetValue();
                                groupsV[0].objects++;
                            }
                    }
                }
            }
            foreach (var g in groupsV)
            {
                if (!g.background || g.baseObjects == g.objects) continue;
                var h = (g.background.offsetMin.y - g.background.offsetMax.y) / g.baseObjects * (g.objects - g.baseObjects);
                g.background.offsetMin += new Vector2(0, h);
                g.difference = h;
            }
            foreach (var g in groupsH)
            {
                if (!g.background || g.baseObjects == g.objects) continue;
                var h = (g.background.offsetMax.x - g.background.offsetMin.x) / g.baseObjects * (g.objects - g.baseObjects);
                g.background.offsetMax += new Vector2(h, 0);
                g.difference = h;
            }
        }
        public void RemoveOptions()
        {
            foreach (var o in addedObjects)
                if (o)
                    DestroyImmediate(o);
            addedObjects.Clear();
            foreach (var g in groupsV)
                if (g.background && g.baseObjects != g.objects)
                    g.background.offsetMin -= new Vector2(0, g.difference);
            foreach (var g in groupsH)
                if (g.background && g.baseObjects != g.objects)
                    g.background.offsetMax -= new Vector2(g.difference, 0);
            groupsV.Clear();
            groupsH.Clear();
        }

        public void SortItems(List<(Item_Base, Item_Base, bool)> list)
        {
            list.Sort(new Comparison<(Item_Base, Item_Base, bool)>(
                (x, y) =>
                  (y.Item1.UniqueIndex == x.Item2.UniqueIndex ? 1 : (y.Item2.UniqueIndex == x.Item1.UniqueIndex ? -1 : 0))
                + (y.Item3 == x.Item3 ? 0 : y.Item3 ? 2 : -2)));
        }

    }

    class group
    {
        public RectTransform background;
        public int objects = 0;
        public int baseObjects = 0;
        public float difference;
    }

    public class ModHandler
    {
        public static readonly Dictionary<string, List<MethodInfo>> interfaceMethods;
        static ModHandler() {
            interfaceMethods = new Dictionary<string, List<MethodInfo>>();
            foreach (var m in typeof(ModHandler).GetMethods(~BindingFlags.Default))
                if (m.Name.ToLower().StartsWith("int_"))
                {
                    var n = m.Name.ToLowerInvariant().Remove(0, "int_".Length);
                    if (interfaceMethods.TryGetValue(n, out var l))
                        l.Add(m);
                    else
                        interfaceMethods.Add(n, new List<MethodInfo> { m });
                }
        }
        public readonly Mod parent;
        readonly Traverse messageReciever;
        public readonly List<NetworkChannel> channels = new List<NetworkChannel>();
        public static List<NetworkChannel> allChannels = new List<NetworkChannel>();
        public Dictionary<Type, Serializer> serializers = new Dictionary<Type, Serializer>()
        {
            [typeof(bool)] = new Serializer(x => BitConverter.GetBytes((bool)x), x => BitConverter.ToBoolean(x, 0), 1),
            [typeof(byte)] = new Serializer(x => new[] { (byte)x }, x => x[0], 1),
            [typeof(sbyte)] = new Serializer(x => new[] { unchecked((byte)(sbyte)x) }, x => unchecked((sbyte)x[0]), 1),
            [typeof(short)] = new Serializer(x => BitConverter.GetBytes((short)x), x => BitConverter.ToInt16(x, 0), 2),
            [typeof(ushort)] = new Serializer(x => BitConverter.GetBytes((ushort)x), x => BitConverter.ToUInt16(x, 0), 2),
            [typeof(int)] = new Serializer(x => BitConverter.GetBytes((int)x), x => BitConverter.ToInt32(x, 0), 4),
            [typeof(uint)] = new Serializer(x => BitConverter.GetBytes((uint)x), x => BitConverter.ToUInt32(x, 0), 4),
            [typeof(long)] = new Serializer(x => BitConverter.GetBytes((long)x), x => BitConverter.ToInt64(x, 0), 8),
            [typeof(ulong)] = new Serializer(x => BitConverter.GetBytes((ulong)x), x => BitConverter.ToUInt64(x, 0), 8),
            [typeof(float)] = new Serializer(x => BitConverter.GetBytes((float)x), x => BitConverter.ToSingle(x, 0), 4),
            [typeof(double)] = new Serializer(x => BitConverter.GetBytes((double)x), x => BitConverter.ToDouble(x, 0), 8),
            [typeof(char)] = new Serializer(x => BitConverter.GetBytes((char)x), x => BitConverter.ToChar(x, 0), 2),
            [typeof(string)] = new Serializer(x => {
                var data = new List<byte>();
                foreach (char chr in (string)x)
                    data.AddRange(BitConverter.GetBytes(chr));
                return data.ToArray();
            }, x => {
                string str = "";
                for (int i = 0; i < x.Length / 2; i++)
                    str += BitConverter.ToChar(x, i * 2);
                return str;
            }),
            [typeof(Vector2)] = new Serializer(x => {
                var b = new byte[8];
                var v = (Vector2)x;
                BitConverter.GetBytes(v.x).CopyTo(b, 0);
                BitConverter.GetBytes(v.y).CopyTo(b, 4);
                return b;
            }, x => new Vector2(BitConverter.ToSingle(x, 0), BitConverter.ToSingle(x, 4)), 8),
            [typeof(Vector3)] = new Serializer(x => {
                var b = new byte[12];
                var v = (Vector3)x;
                BitConverter.GetBytes(v.x).CopyTo(b, 0);
                BitConverter.GetBytes(v.y).CopyTo(b, 4);
                BitConverter.GetBytes(v.z).CopyTo(b, 8);
                return b;
            }, x => new Vector3(BitConverter.ToSingle(x, 0), BitConverter.ToSingle(x, 4), BitConverter.ToSingle(x, 8)), 12),
            [typeof(Vector4)] = new Serializer(x => {
                var b = new byte[16];
                var v = (Vector4)x;
                BitConverter.GetBytes(v.x).CopyTo(b, 0);
                BitConverter.GetBytes(v.y).CopyTo(b, 4);
                BitConverter.GetBytes(v.z).CopyTo(b, 8);
                BitConverter.GetBytes(v.w).CopyTo(b, 12);
                return b;
            }, x => new Vector4(BitConverter.ToSingle(x, 0), BitConverter.ToSingle(x, 4), BitConverter.ToSingle(x, 8), BitConverter.ToSingle(x, 12)), 16),
            [typeof(Quaternion)] = new Serializer(x => {
                var b = new byte[16];
                var v = (Quaternion)x;
                BitConverter.GetBytes(v.x).CopyTo(b, 0);
                BitConverter.GetBytes(v.y).CopyTo(b, 4);
                BitConverter.GetBytes(v.z).CopyTo(b, 8);
                BitConverter.GetBytes(v.w).CopyTo(b, 12);
                return b;
            }, x => new Quaternion(BitConverter.ToSingle(x, 0), BitConverter.ToSingle(x, 4), BitConverter.ToSingle(x, 8), BitConverter.ToSingle(x, 12)), 16)
        };
        public Dictionary<Type, Serializer> customSerializers = new Dictionary<Type, Serializer>();
        const string urlStart = "https://www.raftmodding.com/api/v1/mods/";
        const string urlEnd = "/version.txt";
        public ModHandler(Mod mod)
        {
            parent = mod;
            ModSlug = string.IsNullOrWhiteSpace(mod.updateUrl) || !(mod.updateUrl.Length > urlStart.Length + urlEnd.Length && mod.updateUrl.StartsWith(urlStart) && mod.updateUrl.EndsWith(urlEnd)) ? "" : mod.updateUrl.Remove(mod.updateUrl.Length - urlEnd.Length).Remove(0,urlStart.Length);
            if (ModSlug == "YOUR-MOD-SLUG-HERE" || ModSlug == "")
                ModSlug = mod.GetModInfo().name.ToLowerInvariant().Replace(" ", "-");
            messageReciever = Traverse.Create(mod);
            var recieverField = messageReciever.Field("ModUtils_Reciever");
            if (recieverField.FieldExists())
            {
                if (recieverField.GetValue() == null)
                    try
                    {
                        var fType = recieverField.GetValueType();
                        if (fType == typeof(Type))
                            throw new InvalidOperationException("Cannot create instance of class " + fType.FullName);
                        if (fType.IsAbstract)
                            throw new InvalidOperationException("Cannot create instance of abstract class " + fType.FullName);
                        if (fType.IsInterface)
                            throw new InvalidOperationException("Cannot create instance of interface class " + fType.FullName);
                        var c = fType.GetConstructors((BindingFlags)(-1)).FirstOrDefault(x => x.GetParameters().Length == 0);
                        if (c == null)
                            throw new MissingMethodException("No parameterless constructor found for class " + fType.FullName);
                        else
                            recieverField.SetValue(c.Invoke(new object[0]));
                    }
                    catch (Exception e)
                    {
                        Debug.Log($"[ModUtils]: Found reciever field of mod {parent.modlistEntry.jsonmodinfo.name}'s main class but failed to create an instance for it. You may need to create the class instance yourself.\n{(e is TargetInvocationException ? e.InnerException : e)}");
                    }
                if (recieverField.GetValue() != null)
                {
                    if (recieverField.GetValue() is Type)
                        messageReciever = Traverse.Create((Type)recieverField.GetValue());
                    else
                        messageReciever = Traverse.Create(recieverField.GetValue());
                }
            }
            var channel = messageReciever.Field("ModUtils_Channel").GetValue();
            if (channel == null) { }
            else if (channel is NetworkChannel)
                channels.Add((NetworkChannel)channel);
            else if (channel is IEnumerable<NetworkChannel>)
                channels.AddRangeUniqueOnly((IEnumerable<NetworkChannel>)channel);
            else
                channels.AddRangeUniqueOnly(TryCastObjectToChannel(channel));
            allChannels.AddRangeUniqueOnly(channels);
            Transpiler.modClass = parent.GetType();
            var patchedMethods = new HashSet<MethodInfo>();
            var recieverType = messageReciever.GetValue() as Type ?? messageReciever.GetValue().GetType();
            foreach (var method in recieverType.GetMethods(~BindingFlags.Default))
                if (method.Name.ToLower().StartsWith("modutils_") && !calls.Contains(method.Name.ToLowerInvariant()))
                    try
                    {
                        if (interfaceMethods.TryGetValue(method.Name.ToLowerInvariant().Remove(0, "modutils_".Length), out var l))
                        {
                            if (Transpiler.FindPatch(method, l, out var m1))
                                try
                                {
                                    Transpiler.newMethod = m1;
                                    Main.instance.harmony.Patch(method, transpiler: new HarmonyMethod(typeof(Transpiler), nameof(Transpiler.Transpile)));
                                    patchedMethods.Add(method);
                                }
                                catch (Exception e)
                                {
                                    Debug.LogError($"[ModUtils]: An error occured while trying to implement the {method} method for the {parent.modlistEntry.jsonmodinfo.name} mod\n{e}");
                                }
                            else
                                Debug.LogWarning("[ModUtils]: Could not find suitable handling for method " + method + ". You may have misspelled the method name, used the wrong parameter types or not meant to implement ModUtils here. Methods found matching the name are:" + l.Join(x => $"\n{x.ReturnType.Name} {x.Name}({Transpiler.GetParameters(x).Join(y => $"{y.ParameterType.Name} {y.Name}")})"));
                        }
                        else
                            Debug.LogWarning("[ModUtils]: Could not find suitable handling for method " + method.Name + ". You may have misspelled the method name or not meant to implement ModUtils here");
                    } catch (Exception e) { Debug.LogError($"[ModUtils]: An unexpected error occured while trying to get patching information for {method}\n{e}"); }
            Patch_ReplaceAPICalls.methodsToLookFor = patchedMethods;
            foreach (var m in Patch_ReplaceAPICalls.TargetMethods(parent.GetType().Assembly))
                Main.instance.harmony.Patch(m, transpiler: new HarmonyMethod(typeof(Patch_ReplaceAPICalls), nameof(Patch_ReplaceAPICalls.Transpiler)));
        }

        void int_RegisterSerializer(Type type, Func<object, byte[]> toBytes, Func<byte[], object> fromBytes) => customSerializers[type] = new Serializer(toBytes, fromBytes);
        void int_RegisterSerializer(Type type, Func<object, byte[]> toBytes, Func<byte[], object> fromBytes, int size) => customSerializers[type] = new Serializer(toBytes, fromBytes, size);
        void int_StartListeningToChannel(NetworkChannel channel)
        {
            channels.AddUniqueOnly(channel);
            allChannels.AddUniqueOnly(channel);
        }
        void int_StartListeningToChannels(IEnumerable<NetworkChannel> channels)
        {
            this.channels.AddRangeUniqueOnly(channels);
            allChannels.AddRangeUniqueOnly(channels);
        }
        void int_StopListeningToChannel(NetworkChannel channel)
        {
            channels.Remove(channel);
            Main.RecheckChannel(channel);
        }
        void int_StopListeningToChannels(IEnumerable<NetworkChannel> channels)
        {
            this.channels.RemoveAll(x => channels.Contains(x));
            Main.RecheckChannels(channels);
        }
        void int_StartListeningToChannel(object channel)
        {
            int_StartListeningToChannels(TryCastObjectToChannel(channel));
        }
        void int_StopListeningToChannel(object channel)
        {
            int_StopListeningToChannels(TryCastObjectToChannel(channel));
        }

        List<NetworkChannel> TryCastObjectToChannel(object channel, bool logErrors = true)
        {
            var l = new List<NetworkChannel>();
            if (channel is string)
                try
                {
                    l.Add((NetworkChannel)Enum.Parse(typeof(NetworkChannel), channel as string));
                }
                catch (Exception e)
                {
                    if (logErrors)
                        Debug.LogWarning($"[ModUtils]: Could not parse the channel value as type {typeof(NetworkChannel).FullName}\n{e}");
                }
            else if (channel is IEnumerable<string>)
                foreach (var value in channel as IEnumerable<string>)
                    try
                    {
                        l.Add((NetworkChannel)Enum.Parse(typeof(NetworkChannel), value));
                    }
                    catch (Exception e)
                    {
                        if (logErrors)
                            Debug.LogWarning($"[ModUtils]: Could not parse one of the channel values ({value}) as type {typeof(NetworkChannel).FullName}\n{e}");
                    }
            else if (channel is IConvertible)
                try
                {
                    l.Add((NetworkChannel)(long)(channel as IConvertible).ToType(typeof(long), System.Globalization.NumberFormatInfo.CurrentInfo));
                }
                catch (Exception e)
                {
                    if (logErrors)
                        Debug.LogWarning($"[ModUtils]: Could not parse the channel value as type {typeof(NetworkChannel).FullName}\n{e}");
                }
            else
            {
                var enumerable = channel.GetType().GetInterfaces().FirstOrDefault(x => x.IsConstructedGenericType && x.FullName.StartsWith("System.Collections.Generic.IEnumerable"));
                if (enumerable != null && typeof(IConvertible).IsAssignableFrom(enumerable.GenericTypeArguments[0]))
                    foreach (var value in channel as IEnumerable)
                        try
                        {
                            l.Add((NetworkChannel)(long)(value as IConvertible).ToType(typeof(long), System.Globalization.NumberFormatInfo.CurrentInfo));
                        }
                        catch (Exception e)
                        {
                            if (logErrors)
                                Debug.LogWarning($"[ModUtils]: Could not parse one of the channel values ({value}) as type {typeof(NetworkChannel).FullName}\n{e}");
                        }
                else if (logErrors)
                    Debug.LogWarning($"[ModUtils]: Type {channel.GetType().FullName} cannot be parsed as type {typeof(NetworkChannel).FullName} or {typeof(IEnumerable<NetworkChannel>).FullName}");
            }
            return l;
        }

        public byte[] int_Serialize(object[] values)
        {
            var data = new List<byte>();
            var data2 = new List<byte>();
            var ss = GetSerializer(typeof(string));
            foreach (var o in values)
            {
                if (o == null)
                    data2.AddRange(ss.ToBytes("null"));
                else
                {
                    var s = GetSerializer(o.GetType());
                    if (s == null)
                        throw new FormatException("Could not find suitable serializer for type " + o.GetType().FullName + ". To register a new serializer, implement and use the ModUtils_RegisterSerializer method");
                    data.AddRange(s.ToBytes(o));
                    data2.AddRange(ss.ToBytes(o.GetType().FullName));
                }
            }
            data2.InsertRange(0, GetSerializer(typeof(int)).ToBytes(values.Length));
            data2.AddRange(data);
            if (data2.Count % 2 == 1)
                data2.Add(0);
            return data2.ToArray();
        }
        string Serialized(byte[] values)
        {
            string str = "";
            for (int i = 0; i < values.Length / 2; i++)
                str += BitConverter.ToChar(values, i * 2);
            return str;
        }
        string int_StringSerialize(object[] values) => Serialized(int_Serialize(values));
        public object[] int_Deserialize(byte[] data) => int_Deserialize(data, true);
        object[] int_Deserialize(byte[] data, bool logErrors)
        {
            var pos = 0;
            var count = (int)GetSerializer(typeof(int)).ToObject(data, ref pos);
            var types = new Type[count];
            var objs = new object[count];
            var ss = GetSerializer(typeof(string));
            for (int i = 0; i < count; i++)
            {
                var n = (string)ss.ToObject(data, ref pos);
                types[i] = AccessTools.TypeByName(n);
                if (n != "null" && types[i] == null && logErrors)
                    Debug.LogError($"[ModUtils]: Message parse failed to fetch type: " + n + "\n" + Environment.StackTrace);
            }
            for (int i = 0; i < count; i++)
            {
                if (types[i] == null)
                    continue;
                var s = GetSerializer(types[i]);
                if (s == null)
                {
                    if (logErrors)
                        Debug.LogError("[ModUtils]: Could not find suitable serializer for type " + types[i].FullName + ". Message may belong to a different mod or you may be using an different version to another player\n" + Environment.StackTrace);
                }
                else
                    objs[i] = s.ToObject(data, ref pos);
            }
            return objs;
        }
        object[] int_Deserialize(string serial) => int_Deserialize(serial, true);
        object[] int_Deserialize(string serial, bool logErrors)
        {
            var bytes = new List<byte>();
            foreach (var c in serial)
                bytes.AddRange(BitConverter.GetBytes(c));
            return int_Deserialize(bytes.ToArray(), logErrors);
        }
        Message int_CreateGenericMessage(Messages messageType, int genericId, object[] values) => new Message_InitiateConnection(messageType, genericId, int_StringSerialize(values));
        int int_GetGenericMessageId(Message message) => int_GetGenericMessageId(message, true);
        int int_GetGenericMessageId(Message message, bool logErrors)
        {
            var m = message as Message_InitiateConnection;
            if (m == null)
            {
                if (logErrors)
                    Debug.LogError("[ModUtils]: Message is not a generic message\n" + Environment.StackTrace);
                return 0;
            }
            return m.appBuildID;
        }
        object[] int_GetGenericMessageValues(Message message) => int_GetGenericMessageValues(message, true);
        object[] int_GetGenericMessageValues(Message message, bool logErrors)
        {
            if (!(message is Message_InitiateConnection))
            {
                if (logErrors)
                    Debug.LogError("[ModUtils]: Message is not a generic message\n" + Environment.StackTrace);
                return null;
            }
            try
            {
                return int_Deserialize((message as Message_InitiateConnection).password, logErrors);
            } catch (Exception e)
            {
                if (logErrors)
                    Debug.LogError(e);
                return null;
            }
        }
        void int_ReloadBuildMenu()
        {
            Main.instance.RemoveOptions();
            Main.instance.InsertOptions();
        }

        Serializer GetSerializer(Type type)
        {
            foreach (var p in customSerializers)
                if (p.Key == type)
                    return p.Value;
            if (type.IsEnum)
                return new EnumSerializer(GetSerializer(type.GetEnumUnderlyingType()), type);
            foreach (var p in serializers)
                if (p.Key == type)
                    return p.Value;
            var enumerable = type.GetInterfaces().FirstOrDefault(x => x.IsConstructedGenericType && x.FullName.StartsWith("System.Collections.Generic.IEnumerable"));
            if (enumerable != null && enumerable.GenericTypeArguments[0] != typeof(object))
                return new CollectionSerializer(GetSerializer(enumerable.GenericTypeArguments[0]), enumerable.GenericTypeArguments[0]);
            if (type.GetInterfaces().Contains(typeof(IEnumerable)))
                return new ObjectCollectionSerializer(this);
            return null;
        }

        static class Transpiler
        {
            public static Type modClass;
            public static MethodInfo newMethod;

            public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase method)
            {
                var bArgs = GetArguments(method);
                var nArgs = GetArguments(newMethod);
                var lastParamInd = bArgs.Count - 1;
                var requiresMod = nArgs.Count > 0 ? typeof(Mod).IsAssignableFrom(nArgs[0]) : false;
                var requiresHandler = nArgs.Count > 0 ? typeof(ModHandler).IsAssignableFrom(nArgs[0]) : false;
                var hasMod = bArgs.Count > 0 ? typeof(Mod).IsAssignableFrom(bArgs[0]) : false;
                var hasHandler = bArgs.Count > 0 ? typeof(ModHandler).IsAssignableFrom(bArgs[0]) : false;
                int arg = 0;
                CodeInstruction GetArg(int index) => (index >= 0 && index <= 3) ? new CodeInstruction(new[] { OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Ldarg_2, OpCodes.Ldarg_3 }[index]) : new CodeInstruction(OpCodes.Ldarg_S, index);
                var code = new List<CodeInstruction>();
                if ((requiresHandler && hasHandler) || (requiresMod && hasMod))
                    code.Add(GetArg(arg++));
                else if (hasHandler && requiresMod)
                {
                    code.AddRange(new[]
                    {
                        GetArg(arg++),
                        new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(ModHandler),nameof(parent)))
                    });
                }
                else if (requiresMod || requiresHandler)
                {
                    if (hasMod)
                        code.Add(GetArg(arg++));
                    else
                        code.AddRange(new[]
                            {
                                new CodeInstruction(OpCodes.Ldtoken,modClass),
                                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Type),"GetTypeFromHandle")),
                                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Main),nameof(Main.GetMod)))
                            });
                    if (requiresHandler)
                        code.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Main), nameof(Main.GetHandler))));
                } else if (hasMod || hasHandler)
                    arg++;
                for (int i = arg; i <= lastParamInd; i++)
                    code.Add(GetArg(i));
                code.AddRange(new[]
                {
                    new CodeInstruction(OpCodes.Call, newMethod),
                    new CodeInstruction(OpCodes.Ret)
                });
                return code;
            }

            public static bool FindPatch(MethodInfo baseMethod, IEnumerable<MethodInfo> newMethods, out MethodInfo found)
            {
                var p = baseMethod.GetParameters();
                foreach (var m in newMethods)
                {
                    var p2 = GetParameters(m);
                    if (p.Length == p2.Count && baseMethod.ReturnType.IsAssignableFrom(m.ReturnType))
                    {
                        var flag = true;
                        for (int i = 0; i < p.Length; i++)
                            if (!p2[i].ParameterType.IsAssignableFrom(p[i].ParameterType))
                            {
                                flag = false;
                                break;
                            }
                        if (flag)
                        {
                            found = m;
                            return true;
                        }
                    }
                }
                found = null;
                return false;
            }
            public static List<ParameterInfo> GetParameters(MethodInfo method)
            {
                var p = method.GetParameters().ToList();
                if (!method.IsStatic && p.Count > 0 && (typeof(Mod).IsAssignableFrom(p[0].ParameterType) || typeof(ModHandler).IsAssignableFrom(p[0].ParameterType)))
                    p.RemoveAt(0);
                return p;
            }

            static List<Type> GetArguments(MethodBase method)
            {
                var l = new List<Type>();
                if (!method.IsStatic)
                    l.Add(method.DeclaringType);
                foreach (var p in method.GetParameters())
                    l.Add(p.ParameterType);
                return l;
            }
        }

        public static readonly string[] calls = new[] { "modutils_modunloaded", "modutils_modloaded", "modutils_messagerecieved", "modutils_buildmenuitems", "modutils_savelocaldata", "modutils_loadlocaldata", "modutils_saveremotedata", "modutils_loadremotedata" };
        public void Unload(Mod mod) => messageReciever.Method("ModUtils_ModUnloaded", mod).GetValue();
        public void Load(Mod mod) => messageReciever.Method("ModUtils_ModLoaded", mod).GetValue();
        public bool ProcessMessage(CSteamID steamID, NetworkChannel channel, Message message)
        {
            var r = messageReciever.Method("ModUtils_MessageRecieved", steamID, channel, message).GetValue();
            return r is bool ? (bool)r : false;
        }
        public string ModSlug;
        public string OldSlug => messageReciever.Field("ModUtils_OldModSlug").GetValue()?.ToString();
        public RGD LocalSave
        {
            get
            {
                var r = messageReciever.Method("ModUtils_SaveLocalData").GetValue();
                return r as RGD;
            }
            set => messageReciever.Method("ModUtils_LoadLocalData", value).GetValue();
        }
        public Message RemoteSave
        {
            get
            {
                var r = messageReciever.Method("ModUtils_SaveRemoteData").GetValue();
                return r as Message;
            }
            set => messageReciever.Method("ModUtils_LoadRemoteData", value).GetValue();
        }
        public Message GetRemoteSave()
        {
            var r = messageReciever.Method("ModUtils_SendRemoteData").GetValue();
            return r as Message;
        }
        public void LoadLocalSave(Message data) => messageReciever.Method("ModUtils_RecieveMessageData", data).GetValue();
        public IEnumerable<(Item_Base, Item_Base, bool)> GetBuildMenuItems()
        {
            var r = messageReciever.Method("ModUtils_BuildMenuItems").GetValue();
            if (r is IEnumerable<(Item_Base, Item_Base, bool)>)
                return (IEnumerable<(Item_Base, Item_Base, bool)>)r;
            if (r is IEnumerable<(Item_Base, Item_Base)>)
            {
                var l = new List<(Item_Base, Item_Base, bool)>();
                foreach (var i in (IEnumerable<(Item_Base, Item_Base)>)r)
                    l.Add((i.Item1, i.Item2, false));
                return l;
            }
            if (r is IEnumerable<Tuple<Item_Base, Item_Base,bool>>)
            {
                var l = new List<(Item_Base, Item_Base, bool)>();
                foreach (var i in (IEnumerable<Tuple<Item_Base, Item_Base,bool>>)r)
                    l.Add((i.Item1, i.Item2, i.Item3));
                return l;
            }
            if (r is IEnumerable<Tuple<Item_Base, Item_Base>>)
            {
                var l = new List<(Item_Base, Item_Base, bool)>();
                foreach (var i in (IEnumerable<Tuple<Item_Base, Item_Base>>)r)
                    l.Add((i.Item1, i.Item2, false));
                return l;
            }
            if (r is IDictionary<Item_Base, Item_Base>)
            {
                var l = new List<(Item_Base, Item_Base, bool)>();
                foreach (var i in (IDictionary<Item_Base, Item_Base>)r)
                    l.Add((i.Value, i.Key, false));
                return l;
            }
            if (r is IDictionary<Item_Base, (Item_Base,bool)>)
            {
                var l = new List<(Item_Base, Item_Base, bool)>();
                foreach (var i in (IDictionary<Item_Base, (Item_Base, bool)>)r)
                    l.Add((i.Value.Item1, i.Key, i.Value.Item2));
                return l;
            }
            return null;
        }

        public static implicit operator ModHandler(Mod mod) => new ModHandler(mod);
    }

    [HarmonyPatch(typeof(Transform), "parent", MethodType.Setter)]
    public class Patch_ModLoad
    {
        static void Postfix(Transform __instance, Transform __0)
        {
            if (__0 == ModManagerPage.ModsGameObjectParent.transform)
            {
                __instance.gameObject.AddComponent<WaitForAwake>().onAwake = delegate
                {
                    Main.modLoaded(__instance.GetComponent<Mod>());
                };
            }
        }
    }

    [HarmonyPatch(typeof(BaseModHandler), "UnloadMod")]
    public class Patch_ModUnload
    {
        static void Postfix(ModData moddata)
        {
            Main.modUnloaded(moddata.modinfo.mainClass);
        }
    }

    [HarmonyPatch(typeof(GameMenu), "Open")]
    public class Patch_OpenMenu
    {
        static void Postfix(GameMenu __instance)
        {
            if (__instance.menuType == MenuType.BuildMenu)
                Main.instance.InsertOptions();
        }
    }

    static class SaveHandler
    {
        const string saveName = "\\/ModUtils_CustomWorldSaveData\\/";
        public static RGD CreateLocal()
        {
            var save = CreateObject<RGD_Game>();
            save.t = 255;
            save.name = saveName;
            save.behaviours = new List<RGD>();
            foreach (var l in Main.loaded)
            {
                var d = l.LocalSave;
                if (d != null)
                {
                    var o = CreateObject<RGD_TextWriterObject>();
                    o.text = l.ModSlug;
                    save.behaviours.Add(o);
                    save.behaviours.Add(d);
                }
            }
            return save;
        }
        public static bool TryRestoreLocal(RGD data)
        {
            var g = data as RGD_Game;
            if (g?.name == saveName && g.t == 255 && g.behaviours != null && g.behaviours.Count % 2 == 0)
            {
                var d = new Dictionary<string, RGD>();
                for (int i = 0; i < g.behaviours.Count; i += 2)
                    if (g.behaviours[i] is RGD_TextWriterObject)
                        d[(g.behaviours[i] as RGD_TextWriterObject).text] = g.behaviours[i + 1];
                    else
                        return false;
                if (d.Count > 0)
                    foreach (var l in Main.loaded)
                        if (d.TryGetValue(l.ModSlug, out var s))
                        {
                            l.LocalSave = s;
                            d.Remove(l.ModSlug);
                            if (d.Count == 0)
                                break;
                        }
                if (d.Count > 0)
                    foreach (var l in Main.loaded)
                    {
                        var n = l.OldSlug;
                        if (n != null && d.TryGetValue(l.OldSlug, out var s))
                        {
                            l.LocalSave = s;
                            d.Remove(l.OldSlug);
                            if (d.Count == 0)
                                break;
                        }
                    }
                return true;
            }
            return false;
        }

        public static List<Message> CreateRemote()
        {
            var save = new List<Message>();
            var start = CreateObject<Message_WorldInfo>();
            start.worldGuid = saveName;
            start.Type = ~Messages.NOTHING;
            save.Add(start);
            foreach (var l in Main.loaded)
            {
                var d = l.RemoteSave;
                if (d != null)
                {
                    var o = CreateObject<Message_WorldInfo>();
                    o.worldGuid = l.ModSlug;
                    save.Add(o);
                    save.Add(d);
                }
            }
            start.traderReputation = save.Count - 1;
            return save;
        }

        static List<Message> caught;
        static int waiting = 0;
        public static bool TryRestoreRemote(Message message)
        {
            if (waiting > 0)
            {
                waiting--;
                caught.Add(message);
                if (waiting == 0)
                {
                    var d = new Dictionary<string, Message>();
                    for (int j = 0; j < caught.Count; j += 2)
                        if (caught[j] is Message_WorldInfo)
                            d[(caught[j] as Message_WorldInfo).worldGuid] = caught[j + 1];
                    if (d.Count > 0)
                        foreach (var l in Main.loaded)
                            if (d.TryGetValue(l.ModSlug, out var s))
                            {
                                l.RemoteSave = s;
                                d.Remove(l.ModSlug);
                                if (d.Count == 0)
                                    break;
                            }
                }
                return true;
            }
            var i = message as Message_WorldInfo;
            if (i?.worldGuid == saveName && i.Type == ~Messages.NOTHING)
            {
                caught = new List<Message>();
                waiting = i.traderReputation;
                return true;
            }
            return false;
        }
        public static T CreateObject<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
    }

    [HarmonyPatch(typeof(SaveAndLoad), "RestoreRGDGame")]
    static class Patch_LoadGame
    {
        static void Prefix(RGD_Game game)
        {
            if (game?.behaviours != null)
                foreach (var b in game.behaviours)
                    try
                    {
                        if (SaveHandler.TryRestoreLocal(b))
                        {
                            game.behaviours.Remove(b);
                            break;
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError(e);
                    }
        }
    }

    [HarmonyPatch(typeof(SaveAndLoad), "CreateRGDGame")]
    static class Patch_SaveGame
    {
        static void Postfix(RGD_Game __result)
        {
            try
            {
                __result.behaviours?.Insert(0,SaveHandler.CreateLocal());
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
        }
    }

    [HarmonyPatch(typeof(Raft_Network), "GetWorld")]
    static class Patch_SendWorld
    {
        static void Postfix(List<Message> __result)
        {
            try
            {
                __result.InsertRange(0, SaveHandler.CreateRemote());
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
        }
    }

    [HarmonyPatch(typeof(NetworkUpdateManager), "Deserialize")]
    static class Patch_RecieveWorld
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var ind = code.FindIndex(x => x.opcode == OpCodes.Stloc_2);
            code.InsertRange(ind, new[] {
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patch_RecieveWorld), nameof(OnRecieve)))
            });
            return code;
        }
        static Message OnRecieve(Message msg)
        {
            try
            {
                if (SaveHandler.TryRestoreRemote(msg))
                    return null;
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
            return msg;
        }
    }

    static class Patch_ReplaceAPICalls
    {
        public static HashSet<MethodInfo> methodsToLookFor;
        public static IEnumerable<MethodBase> TargetMethods(Assembly assembly)
        {
            var l = new List<MethodBase>();
            foreach (var t in assembly.GetTypes())
                foreach (var m in t.GetMethods(~BindingFlags.Default))
                    try
                    {
                        foreach (var i in PatchProcessor.GetCurrentInstructions(m, out var iL))
                            if (i.opcode == OpCodes.Call && i.operand is MethodInfo method && methodsToLookFor.Contains(method))
                            {
                                l.Add(m);
                                break;
                            }
                    }
                    catch { }
            return l;
        }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            foreach (var i in code)
                if (i.opcode == OpCodes.Call && i.operand is MethodInfo method && methodsToLookFor.Contains(method) && !method.IsStatic)
                    i.opcode = OpCodes.Callvirt;
            return code;
        }
    }

    public class WaitForAwake : MonoBehaviour
    {
        public Action onAwake;
        void Awake()
        {
            onAwake?.Invoke();
            DestroyImmediate(this);
        }
    }
    public class WaitForUpdate : MonoBehaviour
    {
        public Action onUpdate;
        void Update()
        {
            onUpdate?.Invoke();
            DestroyImmediate(this);
        }
    }

    public class Serializer
    {
        Func<object, byte[]> converter;
        Func<byte[], object> reverter;
        public readonly int? size;
        protected Serializer() { }
        public Serializer(Func<object, byte[]> toBytes, Func<byte[], object> fromBytes)
        {
            converter = toBytes;
            reverter = fromBytes;
        }
        public Serializer(Func<object, byte[]> toBytes, Func<byte[], object> fromBytes, int bytesPerObject) : this(toBytes,fromBytes)
        {
            size = bytesPerObject;
        }
        public virtual object ToObject(byte[] source, ref int start)
        {
            int s;
            if (size == null)
            {
                s = BitConverter.ToInt32(source, start);
                start += 4;
            }
            else
                s = size.Value;
            var newBytes = new byte[s];
            for (int i = 0; i < s; i++)
                newBytes[i] = source[start+i];
            start += s;
            try
            {
                return reverter(newBytes);
            }
            catch (Exception e)
            {
                throw new SerializationException(e);
            }
        }
        public virtual byte[] ToBytes(object source)
        {
            byte[] bytes;
            try
            {
                bytes = converter(source);
            }
            catch (Exception e)
            {
                throw new SerializationException(e);
            }
            if (size == null)
            {
                var b = new byte[bytes.Length + 4];
                BitConverter.GetBytes(bytes.Length).CopyTo(b);
                bytes.CopyTo(b, 4);
                return b;
            } else
            {
                if (bytes.Length != size.Value)
                    Array.Resize(ref bytes, size.Value);
                return bytes;
            }
        }
    }

    public class CollectionSerializer : Serializer
    {
        Serializer BaseSerializer;
        Type BaseType;
        public CollectionSerializer(Serializer baseSerializer, Type baseType)
        {
            BaseSerializer = baseSerializer;
            BaseType = baseType;
        }
        public override byte[] ToBytes(object source)
        {
            var l = new List<byte>();
            var i = 0;
            foreach (var o in (IEnumerable)source)
            {
                l.AddRange(BaseSerializer.ToBytes(o));
                i++;
            }
            l.InsertRange(0, BitConverter.GetBytes(i));
            return l.ToArray();
        }
        public override object ToObject(byte[] source, ref int start)
        {
            var c = BitConverter.ToInt32(source, start);
            var objects = BaseType.MakeArrayType().GetConstructor(new[] { typeof(int) }).Invoke(new object[] { c }) as IList;
            start += 4;
            for (var i = 0; i < c; i++)
                objects[i] = BaseSerializer.ToObject(source, ref start);
            return objects;
        }
    }

    class ObjectCollectionSerializer : Serializer
    {
        ModHandler handler;
        public ObjectCollectionSerializer(ModHandler Handler)
        {
            handler = Handler;
        }
        public override byte[] ToBytes(object source)
        {
            var l = new List<object>();
            foreach (var o in (IEnumerable)source)
                l.Add(o);
            var a = handler.int_Serialize(l.ToArray()).ToList();
            a.InsertRange(0, BitConverter.GetBytes(a.Count));
            return a.ToArray();
        }
        public override object ToObject(byte[] source, ref int start)
        {
            var c = BitConverter.ToInt32(source, start);
            var a = new byte[c];
            start += 4;
            for (int i = 0; i < c; i++)
                a[i] = source[i + start];
            start += c;
            return handler.int_Deserialize(a);
        }
    }

    public class EnumSerializer : Serializer
    {
        Serializer BaseSerializer;
        Type EnumType;
        public EnumSerializer(Serializer baseSerializer, Type enumType)
        {
            BaseSerializer = baseSerializer;
            EnumType = enumType;
        }
        public override byte[] ToBytes(object source) => BaseSerializer.ToBytes((source as IConvertible).ToType(EnumType.GetEnumUnderlyingType(), System.Globalization.NumberFormatInfo.CurrentInfo));
        public override object ToObject(byte[] source, ref int start) => Enum.ToObject(EnumType, BaseSerializer.ToObject(source, ref start));
    }

    public class SerializationException : Exception
    {
        public SerializationException(Exception exception) : base("",exception) { }
    }

    static class ExtentionMethods
    {
        public static List<Y> Cast<X,Y>(this IEnumerable<X> c, Func<X,Y> caster)
        {
            var l = new List<Y>();
            foreach (var i in c)
                l.Add(caster(i));
            return l;
        }
    }
}