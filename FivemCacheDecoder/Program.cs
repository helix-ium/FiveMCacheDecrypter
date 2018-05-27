using CommandLine;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FivemCacheDecoder
{
    public class Program
    {
        [Verb("list", HelpText = "Decrypt the leveldb and display information.")]
        class ListOptions
        {
            [Option('c', "cachedir", HelpText = "The fivem cache directory", Required = true)]
            public string CacheDirectory { get; set; }
            [Option('w', "workdir", HelpText = "A place to store the decrypted leveldb folder. Default is the current working directory.", Default = "")]
            public string WorkingDirectory { get; set; }
        }
        [Verb("decode", HelpText = "Decrypt the resources and extract the rpfs.")]
        public class DecodeOptions
        {
            [Option('c', "cachedir", HelpText = "The fivem cache priv directory", Required = true)]
            public string CacheDirectory { get; set; }
            [Option('o', "output", HelpText = @"
			The output directory.
			The following replacements can be used to modify the output directory structure.
			%d = last modified day
			%m = last modified month
			%y = last modified year
			%s = server (e.g. 127.0.0.1_30120)
			%h = resource hash
			%n = resource name", Default = "dump/%s/%n")]
            public string OutputDirectory { get; set; }
            [Option('w', "workdir", HelpText = "A place to store the decrypted leveldb folder. Default is the current working directory.")]
            public string WorkingDirectory { get; set; }

            [Option('d', "duplicates", HelpText = "Enables decoding duplicate resources. Without this option, only the latest version of a resource will be decoded. This is used for comparing multiple versions of a resource. You must use the date or hash replacements. For example: -o \"dump/%m-%d/%n\"", Default = false)]
            public bool Duplicates { get; set; }
        }
        [Verb("encode", HelpText = "Pack and encrypt the resources")]
        public class EncodeOptions
        {
            [Option('c', "cachedir", HelpText = "The fivem cache priv directory", Required = true)]
            public string CacheDirectory { get; set; }
            [Option('o', "output", HelpText = @"
			The output directory.
			The following replacements can be used to modify the output directory structure.
			%d = last modified day
			%m = last modified month
			%y = last modified year
			%s = server (e.g. 127.0.0.1_30120)
			%h = resource hash
			%n = resource name", Default = "dump/%s/%n")]
            public string OutputDirectory { get; set; }
            [Option('w', "workdir", HelpText = "A place to store the decrypted leveldb folder. Default is the current working directory.")]
            public string WorkingDirectory { get; set; }

            [Option('d', "dry", HelpText = "Do a dry run without modifying the cache.", Default = false)]
            public bool DryRun { get; set; }
        }
        static void Main(string[] args)
        {

            var parsed = Parser.Default.ParseArguments<DecodeOptions, EncodeOptions>(args);

            parsed.WithParsed<DecodeOptions>(opt =>
            {
                DecodeVerb(opt);
            });
            parsed.WithParsed<EncodeOptions>(opt =>
            {
                EncodeVerb(opt);
            });
        }

        public static void EncodeVerb(EncodeOptions opt)
        {
            var privDir = new DirectoryInfo(Path.Combine(opt.CacheDirectory));
            if (!privDir.Exists)
            {
                Console.WriteLine("Cache directory not found?");
                return;
            }

            var workDir = string.IsNullOrWhiteSpace(opt.WorkingDirectory) ? Environment.CurrentDirectory : opt.WorkingDirectory;
            var dbDir = new DirectoryInfo(Path.Combine(workDir, "db"));

            Console.WriteLine("Reading entries from database");
            var dbEntries = ReadDatabaseEntries(dbDir.FullName, out var errorCount);
            Console.WriteLine("Found {0} entries", dbEntries.Count);
            Console.WriteLine("Errors reading {0} entries", errorCount);

            var entryLookup = dbEntries.ToLookup(e => e.Filename);
            var files = new DirectoryInfo(privDir.FullName).GetFiles();
            var filesLookup = files.ToLookup(f => f.Name);

            var entriesWithFiles = dbEntries.Select(e => new { entry = e, file = filesLookup[e.Filename].FirstOrDefault() }).ToList();

            Console.WriteLine("Skipping {0} entries", entriesWithFiles.Count(e => e.file == null));
            Console.WriteLine("Skipping {0} files", files.Count(e => !entryLookup[e.Name].Any()));
            Console.WriteLine("Checking {0} files", entriesWithFiles.Count(e => e.file != null));

            foreach (var g in entriesWithFiles.Where(e => e.file != null).GroupBy(e => Tuple.Create(e.entry.ResourceName, e.entry.OriginalFilename)))
            {
                var entryAndFile = g.OrderByDescending(e => e.file.LastWriteTimeUtc).First();


                var entry = entryAndFile.entry;
                var outputFn = filesLookup[entry.Filename].First().FullName;
                var input = File.ReadAllBytes(outputFn);
                var output = Decrypt(DecryptStatic(input, out var origIv), entry.Key, entry.IV);


                var outDirStr = Regex.Replace(opt.OutputDirectory, "(%\\w)", res =>
                {
                    switch (res.Groups[1].Value)
                    {
                        case "%h":
                            return entry.Hash;
                        case "%n":
                            return entry.ResourceName;
                        case "%s":
                            var uri = new Uri(entry.From);
                            return uri.Host + "_" + uri.Port;

                    }
                    return res.Groups[1].Value;
                });

                var outDir = new DirectoryInfo(outDirStr);
                if (!outDir.Exists)
                {
                    Console.WriteLine("Skipping: {0}/{1}", entry.ResourceName, entry.OriginalFilename);
                    continue;
                }



                if (Path.GetExtension(entry.OriginalFilename) == ".rpf")
                {
                    var reader = new Rpf2Reader(new MemoryStream(output));
                    reader.Open();
                    var entries = reader.ReadEntries();
                    var rpfDir = new DirectoryInfo(Path.Combine(outDir.FullName, entry.OriginalFilename));
                    if (!rpfDir.Exists)
                    {
                        Console.WriteLine("Skipping rpf: {0}", rpfDir.FullName);
                        continue;
                    }
                    var rpfFiles = rpfDir.GetFiles("*", SearchOption.AllDirectories);

                    var lookup = entries.ToLookup(e => Path.GetFullPath(Path.Combine(rpfDir.FullName, e.FullName)));
                    var matches = entries.Join(rpfFiles, e => Path.GetFullPath(Path.Combine(rpfDir.FullName, e.FullName)), f => f.FullName, (e, f) => new { entry = e, file = f }).ToList();
                    var newFiles = rpfFiles.Where(f => !lookup[f.FullName].Any()).ToList();
                    var differ = rpfFiles.Length != matches.Count;
                    foreach (var m in matches)
                    {
                        var oldHash = Hash(File.ReadAllBytes(m.file.FullName));
                        var newHash = Hash(m.entry.Data);
                        if (oldHash != newHash)
                        {
                            Console.WriteLine("Rpf file change: {0}", m.entry.FullName);
                            differ = true;
                        }
                    }
                    foreach (var nf in newFiles)
                    {
                        Console.WriteLine("Rpf file new: {0}", nf.FullName);
                    }

                    if (differ)
                    {
                        Console.WriteLine("Overwriting {0}", entry.ResourceName);

                        if (!opt.DryRun)
                        {
                            using (var ms = new MemoryStream())
                            {
                                var rpfWr = new Rpf2Writer(ms);
                                rpfWr.Write(rpfDir);
                                File.WriteAllBytes(outputFn, EncryptStatic(Encrypt(ms.ToArray(), entry.Key, entry.IV), origIv));
                            }
                        }
                    }
                }
                else
                {
                    var inFn = Path.Combine(outDir.FullName, entry.OriginalFilename);
                    if (!File.Exists(inFn))
                    {
                        Console.WriteLine("Skipping: {0}", inFn);
                        continue;
                    }
                    var inData = File.ReadAllBytes(inFn);
                    var inHash = Hash(inData);
                    var oldHash = Hash(output);

                    if (inHash != oldHash)
                    {
                        Console.WriteLine("Overwriting {0} with {1}", entry.Filename, inFn);
                        if (!opt.DryRun)
                            File.WriteAllBytes(outputFn, EncryptStatic(Encrypt(inData, entry.Key, entry.IV), origIv));

                    }

                }
            }
            Console.WriteLine("Finished" + (opt.DryRun ? " (Dry Run. Nothing was saved)" : ""));
        }

        static string Hash(byte[] data)
        {
            using (var sha = SHA1Managed.Create())
                return BitConverter.ToString(sha.ComputeHash(data));
        }

        public static void DecodeVerb(DecodeOptions opt)
        {
            var privDir = new DirectoryInfo(Path.Combine(opt.CacheDirectory));
            if (!privDir.Exists)
            {
                Console.WriteLine("Cache directory not found?");
                return;
            }

            var workDir = string.IsNullOrWhiteSpace(opt.WorkingDirectory) ? Environment.CurrentDirectory : opt.WorkingDirectory;
            var dbDir = new DirectoryInfo(Path.Combine(workDir, "db"));

            Console.WriteLine("Decrypting database");
            DecryptDb(new DirectoryInfo(Path.Combine(privDir.FullName, "db")), dbDir);

            Console.WriteLine("Reading entries from database");
            var entries = ReadDatabaseEntries(dbDir.FullName, out var errorCount);
            Console.WriteLine("Found {0} entries", entries.Count);
            Console.WriteLine("Errors reading {0} entries", errorCount);

            var entryLookup = entries.ToLookup(e => e.Filename);
            var files = new DirectoryInfo(privDir.FullName).GetFiles();
            var filesLookup = files.ToLookup(f => f.Name);

            var entriesWithFiles = entries.Select(e => new { entry = e, file = filesLookup[e.Filename].FirstOrDefault() }).ToList();

            Console.WriteLine("Skipping {0} entries", entriesWithFiles.Count(e => e.file == null));
            Console.WriteLine("Skipping {0} files", files.Count(e => !entryLookup[e.Name].Any()));
            Console.WriteLine("Decrypting {0} files", entriesWithFiles.Count(e => e.file != null));

            foreach (var g in entriesWithFiles.Where(e => e.file != null).GroupBy(e => Tuple.Create(e.entry.ResourceName, e.entry.OriginalFilename)))
            {
                foreach (var entryAndFile in g.OrderByDescending(e => e.file.LastWriteTimeUtc).ToList())
                {

                    var entry = entryAndFile.entry;
                    var input = File.ReadAllBytes(filesLookup[entry.Filename].First().FullName);
                    var output = Decrypt(DecryptStatic(input), entry.Key, entry.IV);

                    var timeStamp = entryAndFile.file.LastWriteTimeUtc;

                    var outDirStr = Regex.Replace(opt.OutputDirectory, "(%\\w)", res =>
                    {
                        switch (res.Groups[1].Value)
                        {
                            case "%d":
                                return timeStamp.Day.ToString();
                            case "%m":
                                return timeStamp.Month.ToString();
                            case "%y":
                                return timeStamp.Year.ToString();
                            case "%h":
                                return entry.Hash;
                            case "%n":
                                return entry.ResourceName;
                            case "%s":
                                var uri = new Uri(entry.From);
                                return uri.Host + "_" + uri.Port;

                        }
                        return res.Groups[1].Value;
                    });

                    var outDir = new DirectoryInfo(outDirStr);
                    if (!outDir.Exists)
                        outDir.Create();
                    Directory.SetLastWriteTimeUtc(outDir.FullName, timeStamp);

                    if (Path.GetExtension(entry.OriginalFilename) == ".rpf")
                    {
                        var reader = new Rpf2Reader(new MemoryStream(output));
                        reader.Open();
                        foreach (var rpfEntry in reader.ReadEntries())
                        {
                            var outFn = new FileInfo(Path.Combine(outDir.FullName, entry.OriginalFilename, rpfEntry.FullName));
                            if (!outFn.Directory.Exists)
                                outFn.Directory.Create();
                            File.WriteAllBytes(outFn.FullName, rpfEntry.Data);
                            File.SetLastWriteTimeUtc(outFn.FullName, timeStamp);
                        }
                    }
                    else
                    {
                        var outFn = Path.Combine(outDir.FullName, entry.OriginalFilename);
                        File.WriteAllBytes(outFn, output);
                        File.SetLastWriteTimeUtc(outFn, timeStamp);
                    }

                    if (!opt.Duplicates)
                        break;
                }
            }
            Console.WriteLine("Finished");
        }

        static byte[] TryDecodeByteArray(object o, int size)
        {
            if (o is byte[])
            {
                return (byte[])o;
            }
            if (o is string)
            {
                var str = (string)o;
                if (str.Length == size)
                    return str.ToCharArray().Select(c => (byte)c).ToArray();
                if (str.StartsWith("0x") && str.Length == size * 2 + 2)
                    return HexStringToBytes(str.Substring(2));
                if (str.Length == size * 2)
                    return HexStringToBytes(str);

                var bytes = Encoding.UTF8.GetBytes(str);
                if (bytes.Length == size)
                    return bytes;

            }
            throw new Exception("Unable to determine type");
        }

        public static List<DbEntry> ReadDatabaseEntries(string dbDir, out int errorCount)
        {
            errorCount = 0;
            var entries = new List<DbEntry>();
            using (var db = LevelDB.DB.Open(dbDir))
            using (var it = db.NewIterator(new LevelDB.ReadOptions()))
            {
                it.SeekToFirst();
                while (it.Valid())
                {
                    try
                    {
                        var mr = MsgPack.Unpacking.UnpackDictionary(it.Value().ToArray()).Value.ToDictionary(v => v.Key.AsString(), v => v.Value);
                        var meta = mr["m"].AsDictionary().ToDictionary(v => v.Key.AsString(), v => v.Value);
                        var fn = Regex.Match(mr["fn"].AsString(), "\\w+:/([0-9a-z_]+)").Groups[1].Value;

                        var iv = TryDecodeByteArray(meta["i"].ToObject(), 8);
                        var key = TryDecodeByteArray(meta["k"].ToObject(), 32);

                        entries.Add(new DbEntry
                        {
                            From = meta["from"].ToString(),
                            Hash = mr["h"].ToString(),
                            Filename = fn,
                            OriginalFilename = meta["filename"].ToString(),
                            Key = key,
                            IV = iv,
                            ResourceName = meta["resource"].ToString()
                        });
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                    }
                    it.Next();
                }
            }
            return entries;
        }
        public class DbEntry
        {
            public string From; //Remote url
            public string Filename;
            public string OriginalFilename;
            public string ResourceName;
            public string Hash;
            public byte[] IV;
            public byte[] Key;

        }

        public class Rpf2Writer
        {
            const int align = 2048;
            BinaryWriter _rpf;
            long _entryLoc;
            long _namesBase;
            long _namesLoc;
            long _dataLoc;

            public Rpf2Writer(Stream rpfStream)
            {
                _rpf = new BinaryWriter(rpfStream);
            }

            public void Write(DirectoryInfo dir)
            {

                var start = _rpf.BaseStream.Position;
                _rpf.Write(new byte[align * 2]);
                var entryCount = dir.GetDirectories("*", SearchOption.AllDirectories).Length + dir.GetFiles("*", SearchOption.AllDirectories).Length + 1;
                _entryLoc = align;
                _namesBase = _entryLoc + (entryCount * 16);
                _namesLoc = _namesBase;
                _dataLoc = _rpf.BaseStream.Position;

                _rpf.BaseStream.Position = 0;
                _rpf.Write((int)843468882);
                _rpf.Write((int)_dataLoc);
                _rpf.Write((int)entryCount);

                _rpf.BaseStream.Position = _entryLoc + (0 * 16);
                var count = dir.GetDirectories().Length + dir.GetFiles().Length;
                _rpf.Write((int)(_namesLoc - _namesBase));
                _rpf.Write((int)(count));
                _rpf.Write((uint)(1 | 0x80000000));
                _rpf.Write((int)(count));

                _rpf.BaseStream.Position = _namesLoc;
                WriteStringNullTerm(_rpf, "/");
                _namesLoc = _rpf.BaseStream.Position;


                _Write(1, dir);
            }
            int _Write(int startIdx, DirectoryInfo rootDir)
            {
                var subDirs = rootDir.GetDirectories();
                var files = rootDir.GetFiles();
                var entries = subDirs.Select(d => new { name = d.Name, obj = (object)d }).Concat(files.Select(f => new { name = f.Name, obj = (object)f })).ToList();
                var count = entries.Count;
                var idx = startIdx;

                entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.name, right.name));



                foreach (var ent in entries)
                {
                    var di = ent.obj as DirectoryInfo;
                    var file = ent.obj as FileInfo;
                    if (di != null)
                    {
                        var nameLoc = _rpf.BaseStream.Position = _namesLoc;
                        WriteStringNullTerm(_rpf, di.Name);
                        _namesLoc = _rpf.BaseStream.Position;

                        var subCount = _Write(startIdx + count, di);
                        var ecount = di.GetDirectories().Length + di.GetFiles().Length;

                        _rpf.BaseStream.Position = _entryLoc + (idx * 16);
                        _rpf.Write((int)(nameLoc - _namesBase));
                        _rpf.Write((int)(ecount));
                        _rpf.Write((uint)((startIdx + count) | 0x80000000));
                        _rpf.Write((int)(ecount));
                        idx++;
                        count += subCount;
                    }
                    if (file != null)
                    {
                        var dataLoc = _rpf.BaseStream.Position = _dataLoc;
                        var data = File.ReadAllBytes(file.FullName);
                        var dataSize = data.Length;
                        if (dataSize % align != 0)
                            Array.Resize(ref data, dataSize + (align - dataSize % align));
                        _rpf.Write(data);
                        _dataLoc = _rpf.BaseStream.Position;

                        var nameLoc = _rpf.BaseStream.Position = _namesLoc;
                        WriteStringNullTerm(_rpf, file.Name);
                        _namesLoc = _rpf.BaseStream.Position;

                        _rpf.BaseStream.Position = _entryLoc + (idx * 16);
                        _rpf.Write((int)(nameLoc - _namesBase));
                        _rpf.Write((int)(dataSize));
                        _rpf.Write((uint)dataLoc);
                        _rpf.Write((int)(dataSize));
                        idx++;
                    }
                }
                return count;
            }
        }

        public class Rpf2Reader
        {
            BinaryReader _rpf;
            Rpf2Header _header;
            Rpf2RawEntry[] _entries;
            public Rpf2Reader(Stream rpfStream)
            {
                _rpf = new BinaryReader(rpfStream);
            }

            public void Open()
            {
                _header = Rpf2Header.Read(_rpf);
                if (_header.magic != 0x32465052 || _header.cryptoFlag != 0)
                    throw new Exception("Invalid file");

                _rpf.BaseStream.Seek(2048, SeekOrigin.Begin);

                _entries = new Rpf2RawEntry[_header.numEntries];
                for (var i = 0; i < _entries.Length; i++)
                {
                    _entries[i] = Rpf2RawEntry.Read(_rpf, _entries.Length);
                }

            }

            public Rpf2Entry[] ReadEntries()
            {
                var output = new List<Rpf2Entry>();
                _ReadEntries(0, "", output);
                return output.ToArray();
            }

            private void _ReadEntries(int idx, string curPath, List<Rpf2Entry> output)
            {
                var start = _entries[idx];
                for (var i = 0; i < start.length; i++)
                {
                    var sub = _entries[start.dataOffset + i];
                    if (sub.isDirectory)
                    {
                        _ReadEntries((int)(start.dataOffset + i), curPath + sub.name + "/", output);
                    }
                    else
                    {
                        _rpf.BaseStream.Position = sub.dataOffset;
                        var data = _rpf.ReadBytes((int)sub.length);
                        output.Add(new Rpf2Entry { Name = sub.name, FullName = curPath + sub.name, Data = data });
                    }
                }

            }
        }
        public class Rpf2Entry
        {
            public string Name;
            public string FullName;
            public byte[] Data;
            //public uint Position;
            //public uint Length;
        }
        public class Rpf2RawEntry
        {
            public uint nameOffset;
            public uint length;
            public uint dataOffset;
            public bool isDirectory;
            public uint flags;
            public string name;

            public static Rpf2RawEntry Read(BinaryReader br, int entryCount)
            {
                var no = br.ReadUInt32();
                var len = br.ReadUInt32();
                var dOff = br.ReadUInt32();
                var flags = br.ReadUInt32();

                var pos = br.BaseStream.Position;
                br.BaseStream.Position = 2048 + (16 * entryCount) + no;
                var name = ReadStringNullTerm(br);
                br.BaseStream.Position = pos;

                return new Rpf2RawEntry
                {
                    nameOffset = no,
                    length = len,
                    dataOffset = dOff & 0x7FFFFFFF,
                    isDirectory = (dOff & 0x80000000) != 0,
                    flags = flags,
                    name = name
                };
            }
        }

        public class Rpf2Header
        {
            public uint magic;
            public uint tocSize;
            public uint numEntries;
            public uint unkFlag;
            public uint cryptoFlag;

            public static Rpf2Header Read(BinaryReader br)
            {
                return new Rpf2Header
                {
                    magic = br.ReadUInt32(),
                    tocSize = br.ReadUInt32(),
                    numEntries = br.ReadUInt32(),
                    unkFlag = br.ReadUInt32(),
                    cryptoFlag = br.ReadUInt32(),
                };
            }
        }

        public static void DecryptDb(DirectoryInfo encDir, DirectoryInfo outDir)
        {
            if (!outDir.Exists)
                outDir.Create();
            foreach (var file in encDir.GetFiles("*.*"))
            {
                var input = File.ReadAllBytes(file.FullName);
                File.WriteAllBytes(outDir + "\\" + file.Name, DecryptStatic(input) ?? new byte[0]);
            }
        }

        static byte[] HexStringToBytes(string str)
        {
            if (str.StartsWith("0x"))
                str = str.Substring(2);
            var op = new byte[str.Length / 2];
            for (var i = 0; i < op.Length; i++)
            {
                op[i] = (byte)int.Parse(str.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber);
            }
            return op;
        }
        static byte[] Decrypt(byte[] src, byte[] key, byte[] iv)
        {
            var cce = new ChaChaEngine();
            cce.Init(false, new ParametersWithIV(new KeyParameter(key), iv));

            var bsc = new BufferedStreamCipher(cce);
            return bsc.ProcessBytes(src, 0, src.Length);

        }
        static byte[] DecryptStatic(byte[] src)
        {
            return DecryptStatic(src, out var iv);
        }
        static byte[] DecryptStatic(byte[] src, out byte[] iv)
        {
            var key = new byte[]{
                0xD3, 0x61, 0x57, 0x17, 0xE2, 0x16, 0x3F, 0x70, 0xAC, 0x69, 0x51, 0xB2, 0x7D, 0x7A, 0x0B, 0x86,
                0xD8, 0xE9, 0x3E, 0x16, 0xEA, 0xBF, 0x63, 0x2F, 0xDF, 0xBC, 0xC0, 0x0A, 0x1D, 0x3D, 0x62, 0xD6
            };
            iv = new byte[8];
            Array.Copy(src, iv, 8);

            var cce = new ChaChaEngine();
            cce.Init(false, new ParametersWithIV(new KeyParameter(key), iv));

            var bsc = new BufferedStreamCipher(cce);
            return bsc.ProcessBytes(src, 8, src.Length - 8);

        }

        static byte[] Encrypt(byte[] src, byte[] key, byte[] iv)
        {
            var cce = new ChaChaEngine();
            cce.Init(true, new ParametersWithIV(new KeyParameter(key), iv));

            var bsc = new BufferedStreamCipher(cce);
            return bsc.ProcessBytes(src);

        }
        static byte[] EncryptStatic(byte[] src, byte[] iv = null)
        {
            var key = new byte[]{
                0xD3, 0x61, 0x57, 0x17, 0xE2, 0x16, 0x3F, 0x70, 0xAC, 0x69, 0x51, 0xB2, 0x7D, 0x7A, 0x0B, 0x86,
                0xD8, 0xE9, 0x3E, 0x16, 0xEA, 0xBF, 0x63, 0x2F, 0xDF, 0xBC, 0xC0, 0x0A, 0x1D, 0x3D, 0x62, 0xD6
            };


            if (iv == null)
            {
                iv = new byte[8];
                using (var rng = RandomNumberGenerator.Create())
                    rng.GetBytes(iv);
            }

            var cce = new ChaChaEngine();
            cce.Init(true, new ParametersWithIV(new KeyParameter(key), iv));

            var bsc = new BufferedStreamCipher(cce);
            var output = new byte[src.Length + 8];
            Array.Copy(iv, output, 8);
            bsc.ProcessBytes(src, 0, src.Length, output, 8);
            return output;

        }


        public static string ReadStringNullTerm(BinaryReader br)
        {
            var ret = new StringBuilder();
            byte c;
            while ((c = br.ReadByte()) != 0)
                ret.Append((char)c);
            return ret.ToString();
        }
        public static void WriteStringNullTerm(BinaryWriter bw, string str)
        {
            bw.Write(Encoding.ASCII.GetBytes(str));
            bw.Write((byte)0);
        }

    }
}

