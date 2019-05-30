﻿using AbstractAccountApi;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DirectoryApi
{
    public enum AccountRole
    {
        Other,
        Student,
        Teacher,
        Support,
        Director,
        IT,
    }

    public static class AccountManager
    {
        public static List<Account> Students = new List<Account>();
        public static List<Account> Staff = new List<Account>();


        public static async Task<bool> LoadStudents()
        {
            Students.Clear();
            var result = await Task.Run(() =>
            {
                DirectorySearcher search = Connector.GetSearcher(Connector.StudentPath);
                search.Filter = "(ObjectClass=*)";
                search.SizeLimit = 20000;
                search.PropertiesToLoad.Clear();
                search.PropertiesToLoad.Add("sAMAccountName");
                search.PropertiesToLoad.Add("givenName");
                search.PropertiesToLoad.Add("sn");
                search.PropertiesToLoad.Add("displayname");
                search.PropertiesToLoad.Add("smamailalias");
                search.PropertiesToLoad.Add("smaWisaID");
                search.PropertiesToLoad.Add("smawisaname");
                search.PropertiesToLoad.Add("smaClass");
                search.PropertiesToLoad.Add("userAccountControl");
                SearchResultCollection results;

                try
                {
                    results = search.FindAll();
                }
                catch (DirectoryServicesCOMException e)
                {
                    Connector.Log.AddError(Origin.Directory, e.Message);
                    return false;
                }

                int count = 0;
                foreach (SearchResult r in results)
                {
                    DirectoryEntry entry = r.GetDirectoryEntry();

                    // don't parse OU's
                    if (entry.Name.StartsWith("OU")) continue;
                    Students.Add(new Account(entry));
                    count++;
                }

                Connector.Log.AddMessage(Origin.Directory, "Added " + count.ToString() + " Student Accounts");
                return true;
            });

            return result;
        }

        public static async Task<bool> LoadStaff()
        {
            Staff.Clear();
            var result = await Task.Run(() =>
            {
                DirectorySearcher search = Connector.GetSearcher(Connector.StaffPath);
                search.Filter = "(ObjectClass=*)";
                search.SizeLimit = 20000;
                SearchResultCollection results;

                try
                {
                    results = search.FindAll();
                }
                catch (DirectoryServicesCOMException e)
                {
                    Connector.Log.AddError(Origin.Directory, e.Message);
                    return false;
                }

                int count = 0;
                foreach (SearchResult r in results)
                {
                    DirectoryEntry entry = r.GetDirectoryEntry();

                    // don't parse OU's
                    if (entry.Name.StartsWith("OU")) continue;
                    Staff.Add(new Account(entry));
                    count++;
                }

                Connector.Log.AddMessage(Origin.Directory, "Added " + count.ToString() + " Staff Accounts");
                return true;
            });

            return result;
        }

        public static JObject ToJson()
        {
            JObject result = new JObject();

            var students = new JArray();
            foreach (var account in Students)
            {
                students.Add(account.ToJson());
            }
            result["Students"] = students;

            var staff = new JArray();
            foreach(var account in Staff)
            {
                staff.Add(account.ToJson());
            }
            result["Staff"] = staff;

            return result;
        }

        public static void FromJson(JObject obj)
        {
            Students.Clear();
            Staff.Clear();

            var students = obj["Students"].ToArray();
            foreach(var student in students)
            {
                Students.Add(new Account(student as JObject));
            }

            var staff = obj["Staff"].ToArray();
            foreach(var account in staff)
            {
                Staff.Add(new Account(account as JObject));
            }
        }

        public static bool Exists(string username)
        {
            DirectorySearcher search = Connector.GetSearcher(Connector.AccountPath);
            search.Filter = $"(samaccountname={username})";
            SearchResult result;

            try
            {
                result = search.FindOne();

            }
            catch (DirectoryServicesCOMException)
            {
                return false;
            }
            if (result == null) return false;

            return true;
        }

        public static bool HasAlias(string alias)
        {
            DirectorySearcher search = Connector.GetSearcher(Connector.AccountPath);
            search.Filter = $"(smamailalias={alias})";
            SearchResult result;

            try
            {
                result = search.FindOne();

            }
            catch (DirectoryServicesCOMException)
            {
                return false;
            }
            if (result == null) return false;

            return true;
        }

        public static async Task<Account> Create(string firstname, string lastname, string WisaID, AccountRole role, string classgroup = "")
        {
            return await Task.Run(() =>
            {
                string uid = Connector.CreateNewID(firstname, lastname);
                string alias = Connector.CreateNewAlias(firstname, lastname);

                string path = Connector.GetPath(role, classgroup);
                if (path == null)
                {
                    Connector.Log.AddError(Origin.Directory, "unable to add account for " + firstname + " " + lastname);
                    return null;
                }
                Connector.CreateOUIfneeded(path);

                DirectoryEntry ouEntry = Connector.GetEntry(path);
                if (ouEntry == null)
                {
                    Connector.Log.AddError(Origin.Directory, "Account creation went wrong. (cannot get path: " + path + ")");
                    return null;
                }

                DirectoryEntry childEntry = null;
                int NORMAL_ACCOUNT = 0x200;
                int PWD_NOTREQUIRED = 0x20;
                try
                {
                    childEntry = ouEntry.Children.Add($"CN={uid}", "user");
                    childEntry.Properties["sAMAccountName"].Value = uid;
                    childEntry.Properties["userAccountControl"].Value = NORMAL_ACCOUNT | PWD_NOTREQUIRED;
                    childEntry.CommitChanges();
                    ouEntry.CommitChanges();

                    childEntry.RefreshCache();
                }
                catch (DirectoryServicesCOMException e)
                {
                    Connector.Log.AddError(Origin.Directory, "unable to add account for " + firstname + " " + lastname + ": " + e.Message);
                    return null;
                }

                try
                {
                    childEntry.Properties["givenName"].Value = firstname;
                    childEntry.Properties["sn"].Value = lastname;
                    childEntry.Properties["displayname"].Value = firstname + " " + lastname;
                    //childEntry.Properties["mail"].Value = uid + "@sanctamaria-aarschot.be";
                    childEntry.Properties["userprincipalname"].Value = uid + "@" + Connector.AzureDomain;

                    // TODO: move mail alias to another property so we can get rid of the custom objectClass
                    childEntry.Properties["objectClass"].Add("smaSchoolPerson");
                    childEntry.Properties["smamailalias"].Value = alias;
                    childEntry.Properties["smaWisaID"].Value = WisaID;
                    childEntry.Properties["smawisaname"].Value = WisaID;
                    childEntry.Properties["smaClass"].Value = classgroup;
                    childEntry.CommitChanges();
                    childEntry.RefreshCache();
                }
                catch (DirectoryServicesCOMException e)
                {
                    Connector.Log.AddError(Origin.Directory, "unable to add account for " + firstname + " " + lastname + ": " + e.Message);
                    return null;
                }

                if (role == AccountRole.Student)
                {
                    Students.Add(new Account(childEntry));
                    return Students.Last();
                }
                else
                {
                    Staff.Add(new Account(childEntry));
                    return Staff.Last();
                }
            });

        }
    }
}
