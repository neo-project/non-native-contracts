#pragma warning disable IDE0060

using Neo.Cryptography.ECC;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Attributes;
using Neo.SmartContract.Framework.Native;
using Neo.SmartContract.Framework.Services;
using System;
using System.ComponentModel;
using System.Numerics;

namespace Neo.SmartContract
{
    [ManifestExtra("Author", "The Neo Project")]
    [ManifestExtra("Email", "dev@neo.org")]
    [ManifestExtra("Description", "Neo Name Service")]
    [SupportedStandards("NEP-11")]
    [ContractPermission("*", "onNEP11Payment")]
    [ContractSourceCode("https://github.com/neo-project/non-native-contracts")]
    public sealed class NameService : Framework.SmartContract
    {
        public delegate void OnTransferDelegate(UInt160 from, UInt160 to, BigInteger amount, ByteString tokenId);
        public delegate void OnSetAdminDelegate(string name, UInt160 oldAdmin, UInt160 newAdmin);

        [DisplayName("Transfer")]
        public static event OnTransferDelegate OnTransfer;
        [DisplayName("SetAdmin")]
        public static event OnSetAdminDelegate OnSetAdmin;

        private const byte Prefix_TotalSupply = 0x00;
        private const byte Prefix_Balance = 0x01;
        private const byte Prefix_AccountToken = 0x02;
        private const byte Prefix_RegisterPrice = 0x11;
        private const byte Prefix_Root = 0x20;
        private const byte Prefix_Name = 0x21;
        private const byte Prefix_Record = 0x22;

        private const int NameMaxLength = 255;
        private const ulong MillisecondsInSecond = 1000;
        private const ulong OneYear = 365ul * 24 * 3600 * MillisecondsInSecond;
        private const ulong TenYears = OneYear * 10;
        private const int MaxRecordID = 255;

        [Safe]
        public static string Symbol() => "NNS";

        [Safe]
        public static byte Decimals() => 0;

        [Safe]
        public static BigInteger TotalSupply() => (BigInteger)Storage.Get(Storage.CurrentContext, new byte[] { Prefix_TotalSupply });

        [Safe]
        public static UInt160 OwnerOf(ByteString tokenId)
        {
            StorageMap nameMap = new(Storage.CurrentContext, Prefix_Name);
            NameState token = getNameState(nameMap, tokenId);
            return token.Owner;
        }

        [Safe]
        public static Map<string, object> Properties(ByteString tokenId)
        {
            StorageMap nameMap = new(Storage.CurrentContext, Prefix_Name);
            NameState token = getNameState(nameMap, tokenId);
            Map<string, object> map = new();
            map["name"] = token.Name;
            map["expiration"] = token.Expiration;
            map["admin"] = token.Admin;
            return map;
        }

        [Safe]
        public static BigInteger BalanceOf(UInt160 owner)
        {
            if (owner is null || !owner.IsValid)
                throw new Exception("The argument \"owner\" is invalid.");
            StorageMap balanceMap = new(Storage.CurrentContext, Prefix_Balance);
            return (BigInteger)balanceMap[owner];
        }

        [Safe]
        public static Iterator Tokens()
        {
            StorageMap nameMap = new(Storage.CurrentContext, Prefix_Name);
            return nameMap.Find(FindOptions.ValuesOnly | FindOptions.DeserializeValues | FindOptions.PickField1);
        }

        [Safe]
        public static Iterator TokensOf(UInt160 owner)
        {
            if (owner is null || !owner.IsValid)
                throw new Exception("The argument \"owner\" is invalid.");
            StorageMap accountMap = new(Storage.CurrentContext, Prefix_AccountToken);
            return accountMap.Find(owner, FindOptions.ValuesOnly);
        }

        public static bool Transfer(UInt160 to, ByteString tokenId, object data)
        {
            if (to is null || !to.IsValid)
                throw new Exception("The argument \"to\" is invalid.");
            StorageContext context = Storage.CurrentContext;
            StorageMap balanceMap = new(context, Prefix_Balance);
            StorageMap accountMap = new(context, Prefix_AccountToken);
            StorageMap nameMap = new(context, Prefix_Name);
            ByteString tokenKey = GetKey(tokenId);
            ByteString tokenBytes = nameMap[tokenKey];
            if (tokenBytes is null) throw new InvalidOperationException("Unknown token.");
            NameState token = (NameState)StdLib.Deserialize(tokenBytes);
            token.EnsureNotExpired();
            UInt160 from = token.Owner;
            if (!Runtime.CheckWitness(from)) return false;
            if (from != to)
            {
                //Update token info
                token.Owner = to;
                token.Admin = null;
                nameMap[tokenKey] = StdLib.Serialize(token);

                //Update from account
                BigInteger balance = (BigInteger)balanceMap[from];
                balance--;
                if (balance.IsZero)
                    balanceMap.Delete(from);
                else
                    balanceMap.Put(from, balance);
                accountMap.Delete(from + tokenKey);

                //Update to account
                balance = (BigInteger)balanceMap[to];
                balance++;
                balanceMap.Put(to, balance);
                accountMap[to + tokenKey] = tokenId;
            }
            PostTransfer(from, to, tokenId, data);
            return true;
        }

        public static void Update(ByteString nef, string manifest)
        {
            CheckCommittee();
            ContractManagement.Update(nef, manifest);
        }

        [Safe]
        public static Iterator Roots()
        {
            StorageMap rootMap = new(Storage.CurrentContext, Prefix_Root);
            return rootMap.Find(FindOptions.KeysOnly | FindOptions.RemovePrefix);
        }

        public static void SetPrice(long[] priceList)
        {
            CheckCommittee();
            if (priceList.Length == 0)
                throw new Exception("The price list must contain at least 1 item.");
            if (priceList[0] == -1)
                throw new Exception("The price is out of range.");
            foreach (long price in priceList)
                if (price < -1 || price > 10000_00000000)
                    throw new Exception("The price is out of range.");
            Storage.Put(Storage.CurrentContext, new byte[] { Prefix_RegisterPrice }, StdLib.Serialize(priceList));
        }

        [Safe]
        public static long GetPrice(byte length)
        {
            if (length == 0) throw new Exception("Length cannot be 0.");
            long[] priceList = (long[])StdLib.Deserialize(Storage.Get(Storage.CurrentContext, new byte[] { Prefix_RegisterPrice }));
            if (length >= priceList.Length) length = 0;
            return priceList[length];
        }

        [Safe]
        public static bool IsAvailable(string name)
        {
            StorageContext context = Storage.CurrentContext;
            StorageMap rootMap = new(context, Prefix_Root);
            StorageMap nameMap = new(context, Prefix_Name);
            string[] fragments = SplitAndCheck(name, false);
            if (fragments is null) throw new FormatException("The format of the name is incorrect.");
            if (rootMap[fragments[^1]] is null)  {
                if (fragments.Length != 1) throw new InvalidOperationException("The TLD is not found");
                return true;
            }
            long price = GetPrice((byte)fragments[0].Length);
            if (price < 0) return false;
            return parentExpired(nameMap, 0, fragments);
        }

        public static bool Register(string name, UInt160 owner, string email, int refresh, int retry, int expire, int ttl)
        {
            StorageContext context = Storage.CurrentContext;
            StorageMap balanceMap = new(context, Prefix_Balance);
            StorageMap accountMap = new(context, Prefix_AccountToken);
            StorageMap rootMap = new(context, Prefix_Root);
            StorageMap nameMap = new(context, Prefix_Name);
            StorageMap recordMap = new(context, Prefix_Record);
            string[] fragments = SplitAndCheck(name, false);
            if (fragments is null) throw new FormatException("The format of the name is incorrect.");
            ByteString tld = rootMap[fragments[^1]];
            if (fragments.Length == 1) {
                CheckCommittee();
                if (tld is not null) throw new InvalidOperationException("TLD already exists.");
                rootMap.Put(fragments[^1], 0);
            } else {
                if (tld is null) throw new InvalidOperationException("TLD does not exist.");
                if (parentExpired(nameMap, 1, fragments)) throw new InvalidOperationException("One of the parent domains has expired.");
                ByteString parentKey = GetKey(fragments[1]);
                NameState parent = (NameState)StdLib.Deserialize(nameMap[parentKey]);
                parent.CheckAdmin();
            }
            if (!Runtime.CheckWitness(owner)) throw new InvalidOperationException("No authorization.");
            long price = GetPrice((byte)fragments[0].Length);
            if (price < 0)
                CheckCommittee();
            else
                Runtime.BurnGas(price);
            ByteString tokenKey = GetKey(name);
            ByteString buffer = nameMap[tokenKey];
            NameState token;
            UInt160 oldOwner = null;
            if (buffer is not null)
            {
                token = (NameState)StdLib.Deserialize(buffer);
                if (Runtime.Time < token.Expiration) return false;
                oldOwner = token.Owner;
                BigInteger balance = (BigInteger)balanceMap[oldOwner];
                balance--;
                if (balance.IsZero)
                    balanceMap.Delete(oldOwner);
                else
                    balanceMap.Put(oldOwner, balance);
                accountMap.Delete(oldOwner + tokenKey);

                //clear records
                var allrecords = (Iterator<ByteString>)recordMap.Find(tokenKey, FindOptions.KeysOnly);
                foreach (var key in allrecords)
                {
                    Storage.Delete(context, key);
                }
            }
            else
            {
                byte[] key = new byte[] { Prefix_TotalSupply };
                BigInteger totalSupply = (BigInteger)Storage.Get(context, key);
                Storage.Put(context, key, totalSupply + 1);
            }
            token = new()
            {
                Owner = owner,
                Name = name,
                Expiration = Runtime.Time + (ulong)expire * MillisecondsInSecond,
            };
            nameMap[tokenKey] = StdLib.Serialize(token);
            BigInteger ownerBalance = (BigInteger)balanceMap[owner];
            ownerBalance++;
            balanceMap.Put(owner, ownerBalance);
            PutSoaRecord(recordMap, name, email, refresh, retry, expire, ttl);
            accountMap[owner + tokenKey] = name;
            PostTransfer(oldOwner, owner, name, null);
            return true;
        }

        public static void UpdateSOA(string name, string email, int refresh, int retry, int expire, int ttl)
        {
            if (name.Length > NameMaxLength) throw new FormatException("The format of the name is incorrect.");
            StorageContext context = Storage.CurrentContext;
            StorageMap nameMap = new(context, Prefix_Name);
            StorageMap recordMap = new(context, Prefix_Record);
            NameState token = getNameState(nameMap, name);
            token.CheckAdmin();
            PutSoaRecord(recordMap, name, email, refresh, retry, expire, ttl);
        }

        private static void PutSoaRecord(StorageMap recordMap, string name, string email, int refresh, int retry, int expire, int ttl)
        {
            string data = name + " " + email + " " +
		        StdLib.Itoa(Runtime.Time) + " " +
		        StdLib.Itoa(refresh) + " " +
		        StdLib.Itoa(retry) + " " +
		        StdLib.Itoa(expire) + " " +
		        StdLib.Itoa(ttl);
            string tokenId = tokenIDFromName(name);
            PutRecord(recordMap, tokenId, name, RecordType.SOA, 0, data);
        }

        private static void UpdateSOASerial(StorageMap recordMap, string tokenId)
        {
            byte[] recordKey = GetRecordKey(tokenId, tokenId, RecordType.SOA, 0);
            ByteString buffer = recordMap[recordKey];
            if (buffer is null) throw new InvalidOperationException("Unknown SOA record.");
            RecordState record = (RecordState)StdLib.Deserialize(buffer);
            string[] data = StdLib.StringSplit(record.Data, " ", true);
            if (data.Length != 7) throw new InvalidOperationException("Corrupted SOA record format.");
            data[2] = StdLib.Itoa(Runtime.Time); // update serial
            record.Data = data[0] + " " + data[1] + " " +
                data[2] + " " + data[3] + " " +
                data[4] + " " + data[5] + " " +
                data[6];
            recordMap.PutObject(recordKey, record);
        }

        public static ulong Renew(string name)
        {
            return Renew(name, 1);
        }

        public static ulong Renew(string name, byte years)
        {
            if (years < 1 || years > 10) throw new ArgumentException("The argument `years` is out of range.");
            if (name.Length > NameMaxLength) throw new FormatException("The format of the name is incorrect.");
            string[] fragments = SplitAndCheck(name, false);
            if (fragments is null) throw new FormatException("The format of the name is incorrect.");
            long price = GetPrice((byte)fragments[0].Length);
            if (price < 0)
                CheckCommittee();
            else
                Runtime.BurnGas(price * years);
            StorageMap nameMap = new(Storage.CurrentContext, Prefix_Name);
            NameState token = getNameState(nameMap, name);
            token.Expiration += OneYear * years;
            if (token.Expiration > Runtime.Time + TenYears)
                throw new ArgumentException("You can't renew a domain name for more than 10 years in total.");
            ByteString tokenKey = GetKey(name);
            nameMap[tokenKey] = StdLib.Serialize(token);
            return token.Expiration;
        }

        public static void SetAdmin(string name, UInt160 admin)
        {
            if (name.Length > NameMaxLength) throw new FormatException("The format of the name is incorrect.");
            if (admin is not null && !Runtime.CheckWitness(admin)) throw new InvalidOperationException("No authorization.");
            StorageMap nameMap = new(Storage.CurrentContext, Prefix_Name);
            NameState token = getNameState(nameMap, name);
            if (!Runtime.CheckWitness(token.Owner)) throw new InvalidOperationException("No authorization.");
            UInt160 old = token.Admin;
            token.Admin = admin;
            ByteString tokenKey = GetKey(name);
            nameMap[tokenKey] = StdLib.Serialize(token);
            OnSetAdmin(name, old, admin);
        }

        public static void SetRecord(string name, RecordType type, byte id, string data)
        {
            StorageContext context = Storage.CurrentContext;
            StorageMap nameMap = new(context, Prefix_Name);
            StorageMap recordMap = new(context, Prefix_Record);
            string tokenId = CheckRecord(nameMap, recordMap, name, type, data);
            byte[] recordKey = GetRecordKey(tokenId, name, type, id);
            ByteString buffer = recordMap[recordKey];
            if (buffer is null) throw new InvalidOperationException("Unknown record.");
            PutRecord(recordMap, tokenId, name, type, id, data);
            UpdateSOASerial(recordMap, tokenId);
        }
        
        public static void AddRecord(string name, RecordType type, string data)
        {
            StorageContext context = Storage.CurrentContext;
            StorageMap nameMap = new(context, Prefix_Name);
            StorageMap recordMap = new(context, Prefix_Record);
            string tokenId = CheckRecord(nameMap, recordMap, name, type, data);
            byte[] recordsPrefix = GetRecordsByTypePrefix(tokenId, name, type);
            byte id = 0;
            var records = (Iterator<RecordState>)recordMap.Find(recordsPrefix, FindOptions.ValuesOnly | FindOptions.DeserializeValues);
            foreach (var record in records)
            {
                if (record.Name == name && record.Type == type && record.Data == data) throw new InvalidOperationException("Duplicating record.");
                id++;
            }
            if (id > MaxRecordID) throw new InvalidOperationException("Maximum number of records reached.");
            if (type == RecordType.CNAME && id != 0) throw new InvalidOperationException("Multiple CNAME records.");
            PutRecord(recordMap, tokenId, name, type, id, data);
            UpdateSOASerial(recordMap, tokenId);
        }

        private static string CheckRecord(StorageMap nameMap, StorageMap recordMap, string name, RecordType type, string data)
        {
            string tokenId = tokenIDFromName(name);
            switch (type)
            {
                case RecordType.A:
                    if (!CheckIPv4(data)) throw new FormatException("The format of the A record is incorrect.");
                    break;
                case RecordType.CNAME:
                    if (SplitAndCheck(data, true) is null) throw new FormatException("The format of the CNAME record is incorrect.");
                    break;
                case RecordType.TXT:
                    if (data.Length > 255) throw new FormatException("The format of the TXT record is incorrect.");
                    break;
                case RecordType.AAAA:
                    if (!CheckIPv6(data)) throw new FormatException("The format of the AAAA record is incorrect.");
                    break;
                default:
                    throw new InvalidOperationException("The record type is not supported.");
            }
            NameState token = getNameState(nameMap, tokenId);
            token.CheckAdmin();
            return tokenId;
        }

        private static void PutRecord(StorageMap recordMap, string tokenId, string name, RecordType type, byte id, string data)
        {
            byte[] recordKey = GetRecordKey(tokenId, name, type, id);   
            recordMap.PutObject(recordKey, new RecordState
            {
                Name = name,
                Type = type,
                Data = data,
                ID = id,
            });
        }

        [Safe]
        public static string[] GetRecords(string name, RecordType type)
        {
            StorageContext context = Storage.CurrentContext;
            StorageMap nameMap = new(context, Prefix_Name);
            StorageMap recordMap = new(context, Prefix_Record);
            string tokenId = tokenIDFromName(name);
            getNameState(nameMap, tokenId); // ensure not expired
            return GetRecordsByType(recordMap, tokenId, name, type);
        }

        private static string[] GetRecordsByType(StorageMap recordMap, string tokenId, string name, RecordType type)
        {
            byte[] recordsPrefix = GetRecordsByTypePrefix(tokenId, name, type);
            List<string> result = new List<string>();
            var records = (Iterator<RecordState>)recordMap.Find(recordsPrefix, FindOptions.ValuesOnly | FindOptions.DeserializeValues);
            foreach (RecordState record in records)
            {
                result.Add(record.Data);
            }
            return (string[])result;
        }

        [Safe]
        public static Iterator<RecordState> GetAllRecords(string name)
        {
            StorageContext context = Storage.CurrentContext;
            StorageMap nameMap = new(context, Prefix_Name);
            StorageMap recordMap = new(context, Prefix_Record);
            string tokenId = tokenIDFromName(name);
            getNameState(nameMap, tokenId); // ensure not expired
            byte[] recordsKey = GetRecordsPrefix(tokenId, name);
            return (Iterator<RecordState>)recordMap.Find(recordsKey, FindOptions.ValuesOnly | FindOptions.DeserializeValues);
        }

        public static void DeleteRecords(string name, RecordType type)
        {
            if (type == RecordType.SOA) throw new InvalidOperationException("Forbidden to delete SOA record.");
            StorageContext context = Storage.CurrentContext;
            StorageMap nameMap = new(context, Prefix_Name);
            StorageMap recordMap = new(context, Prefix_Record);
            string tokenId = tokenIDFromName(name);
            NameState token = getNameState(nameMap, tokenId);
            token.CheckAdmin();
            byte[] recordsKey = GetRecordsByTypePrefix(tokenId, name, type);
            var keys = (Iterator<ByteString>)recordMap.Find(recordsKey, FindOptions.KeysOnly);
            foreach (ByteString key in keys)
            {
                Storage.Delete(context, key);
            }
            UpdateSOASerial(recordMap, tokenId);
        }

        [Safe]
        public static string[] Resolve(string name, RecordType type)
        {
            List<string> res = new List<string>();
            return (string[])Resolve(res, name, type, 2);
        }

        private static string[] Resolve(List<string> res, string name, RecordType type, int redirect)
        {
            if (redirect < 0) throw new InvalidOperationException("Too many redirections.");
            if (name.Length == 0) throw new InvalidOperationException("Invalid name.");
            if (name[name.Length - 1] == '.')
            {
                name = name.Substring(0, name.Length - 1);
            }
            string cname = null;
            foreach (RecordState state in GetAllRecords(name))
            {
                if (state.Type == type) res.Add(state.Data);
                if (state.Type == RecordType.CNAME) cname = state.Data;
            }
            if (cname is null || type == RecordType.CNAME) return res;
            return (string[])Resolve(res, cname, type, redirect - 1);
        }

        [DisplayName("_deploy")]
        public static void OnDeployment(object data, bool update)
        {
            if (update) return;
            StorageContext context = Storage.CurrentContext;
            Storage.Put(context, new byte[] { Prefix_TotalSupply }, 0);
            Storage.Put(context, new byte[] { Prefix_RegisterPrice }, StdLib.Serialize(new long[]
            {
                1_00000000, // Prices for all other length domain names.
                -1,         // Domain names with a length of 1 are not open for registration by default.
                -1,         // Domain names with a length of 2 are not open for registration by default.
                -1,         // Domain names with a length of 3 are not open for registration by default.
                -1,         // Domain names with a length of 4 are not open for registration by default.
            }));
        }

        private static void CheckCommittee()
        {
            ECPoint[] committees = NEO.GetCommittee();
            UInt160 committeeMultiSigAddr = Contract.CreateMultisigAccount(committees.Length - (committees.Length - 1) / 2, committees);
            if (!Runtime.CheckWitness(committeeMultiSigAddr))
                throw new InvalidOperationException("No authorization.");
        }

        private static ByteString GetKey(string tokenId)
        {
            return CryptoLib.Ripemd160(tokenId);
        }

        private static byte[] GetRecordKey(string tokenId, string name, RecordType type, byte id)
        {
            byte[] prefix = GetRecordsByTypePrefix(tokenId, name, type);
            return Helper.Concat(prefix, new byte[1]{ id });
        }

        private static byte[] GetRecordsByTypePrefix(string tokenId, string name, RecordType type)
        {
            byte[] recordKey = GetRecordsPrefix(tokenId, name);
            return Helper.Concat(recordKey, ((byte)type).ToByteArray());
        }

        private static byte[] GetRecordsPrefix(string tokenId, string name)
        {
            ByteString tokenKey = GetKey(tokenId);
            return Helper.Concat((byte[])tokenKey, (byte[])GetKey(name));
        }

        private static void PostTransfer(UInt160 from, UInt160 to, ByteString tokenId, object data)
        {
            OnTransfer(from, to, 1, tokenId);
            if (to is not null && ContractManagement.GetContract(to) is not null)
                Contract.Call(to, "onNEP11Payment", CallFlags.All, from, 1, tokenId, data);
        }

        private static bool CheckFragment(string root, bool isRoot)
        {
            int maxLength = isRoot ? 16 : 63;
            if (root.Length == 0 || root.Length > maxLength) return false;
            char c = root[0];
            if (isRoot)
            {
                if (!isAlpha(c)) return false;
            }
            else
            {
                if (!isAlphaNum(c)) return false;
            }
            if (root.Length == 1) return true;
            for (int i = 1; i < root.Length - 1; i++)
            {
                c = root[i];
                if (!(isAlphaNum(c) || c == '-')) return false;
            }
            c = root[root.Length - 1];
            return isAlphaNum(c);
        }

        /// <summary>
        /// Denotes whether provided character is a lowercase letter.
        /// </summary>
        private static bool isAlpha(char c)
        {
            return c >= 'a' && c <= 'z';
        }

        /// <summary>
        /// Denotes whether provided character is a lowercase letter or a number.
        /// </summary>
        private static bool isAlphaNum(char c)
        {
            return isAlpha(c) || c >= '0' && c <= '9';
        }

        private static string[] SplitAndCheck(string name, bool allowMultipleFragments)
        {
            int length = name.Length;
            if (length < 3 || length > NameMaxLength) return null;
            string[] fragments = StdLib.StringSplit(name, ".");
            length = fragments.Length;
            if (length > 8) return null;
            if (length > 2 && !allowMultipleFragments) return null;
            for (int i = 0; i < length; i++)
                if (!CheckFragment(fragments[i], i == length - 1))
                    return null;
            return fragments;
        }

        private static bool CheckIPv4(string ipv4)
        {
            int length = ipv4.Length;
            if (length < 7 || length > 15) return false;
            string[] fragments = StdLib.StringSplit(ipv4, ".");
            length = fragments.Length;
            if (length != 4) return false;
            byte[] numbers = new byte[4];
            for (int i = 0; i < length; i++)
            {
                string fragment = fragments[i];
                if (fragment.Length == 0) return false;
                byte number = byte.Parse(fragment);
                if (number > 0 && fragment[0] == '0') return false;
                if (number == 0 && fragment.Length > 1) return false;
                numbers[i] = number;
            }
            switch (numbers[0])
            {
                case 0:
                case 10:
                case 100 when numbers[1] >= 64 && numbers[1] <= 127:
                case 127:
                case 169 when numbers[1] == 254:
                case 172 when numbers[1] >= 16 && numbers[1] <= 31:
                case 192 when numbers[1] == 0 && numbers[2] == 0:
                case 192 when numbers[1] == 0 && numbers[2] == 2:
                case 192 when numbers[1] == 88 && numbers[2] == 99:
                case 192 when numbers[1] == 168:
                case 198 when numbers[1] >= 18 && numbers[1] <= 19:
                case 198 when numbers[1] == 51 && numbers[2] == 100:
                case 203 when numbers[1] == 0 && numbers[2] == 113:
                case >= 224:
                    return false;
            }
            return numbers[3] switch
            {
                0 or 255 => false,
                _ => true,
            };
        }

        private static bool CheckIPv6(string ipv6)
        {
            int length = ipv6.Length;
            if (length < 2 || length > 39) return false;
            string[] fragments = StdLib.StringSplit(ipv6, ":");
            length = fragments.Length;
            if (length < 3 || length > 8) return false;
            ushort[] numbers = new ushort[8];
            bool isCompressed = false;
            for (int i = 0; i < length; i++)
            {
                string fragment = fragments[i];
                if (fragment.Length == 0)
                {
                    if (i == 0)
                    {
                        if (fragments[1].Length != 0) return false;
                        numbers[0] = 0;
                    }
                    else if (i == length - 1)
                    {
                        if (fragments[i - 1].Length != 0) return false;
                        numbers[7] = 0;
                    }
                    else
                    {
                        if (isCompressed) return false;
                        isCompressed = true;
                        int endIndex = 9 - length + i;
                        for (int j = i; j < endIndex; j++)
                            numbers[j] = 0;
                    }
                }
                else
                {
                    if (fragment.Length > 4) return false;
                    int index = isCompressed ? i + 8 - length : i;
                    numbers[index] = (ushort)(short)StdLib.Atoi(fragment, 16);
                }
            }
            if (length < 8 && !isCompressed) return false;
            ushort number = numbers[0];
            if (number < 0x2000 || number == 0x2002 || number == 0x3ffe || number > 0x3fff)
                return false;
            if (number == 0x2001)
            {
                number = numbers[1];
                if (number < 0x200 || number == 0xdb8) return false;
            }
            return true;
        }
        
        /// <summary>
        /// Checks provided name for validness and returns corresponding token ID.
        /// </summary>
        private static string tokenIDFromName(string name)
        {
            string[] fragments = SplitAndCheck(name, true);
            if (fragments is null) throw new FormatException("The format of the name is incorrect.");
            if (fragments.Length == 1) return name;
            return name[^(fragments[^2].Length + fragments[^1].Length + 1)..];
        }

        /// <summary>
        /// Retrieves NameState from storage and checks that it's not expired as far as the parent domain.
        /// </summary>
        private static NameState getNameState(StorageMap nameMap, string tokenId)
        {
            ByteString tokenBytes = nameMap[GetKey(tokenId)];
            if (tokenBytes is null) throw new InvalidOperationException("Unknown token.");
            NameState token = (NameState)StdLib.Deserialize(tokenBytes);
            token.EnsureNotExpired();
            string[] fragments = StdLib.StringSplit(tokenId, ".");
            if (parentExpired(nameMap, 1, fragments)) throw new InvalidOperationException("Parent domain has expired.");
            return token;
        }

        /// <summary>
        /// Returns true if any domain from fragments doesn't exist or expired.
        /// </summary>
        /// <param name="nameMap">Registered domain names storage map.</param>
        /// <param name="first">The deepest subdomain to check.</param>
        /// <param name="fragments">The array of domain name fragments.</param>
        /// <returns>Whether any domain fragment doesn't exist or expired.</returns>
        private static bool parentExpired(StorageMap nameMap, int first, string[] fragments)
        {
            int last = fragments.Length - 1;
            string name = fragments[last];
            for (int i = last; i >= first; i--) {
                if (i != last) {
                    name = fragments[i] + "." + name;
                }
                ByteString buffer = nameMap[GetKey(name)];
                if (buffer is null) return true;
                NameState token = (NameState)StdLib.Deserialize(buffer);
                if (Runtime.Time >= token.Expiration) return true;
            }
            return false;
        }
    }
}
