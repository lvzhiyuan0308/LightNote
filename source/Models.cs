using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace LightNote {
 [DataContract] public class Note {
  [DataMember] public string Id = Guid.NewGuid().ToString("N");
  [DataMember] public string Title = "新便笺";
  [DataMember] public string Body = "";
  [DataMember] public bool Pinned;
  [DataMember] public int Color;
  [DataMember] public long DueUtc;
  [DataMember] public bool Alerting;
  [DataMember] public long DeletedUtc;
  public bool IsDue(long now) { return DeletedUtc == 0 && DueUtc > 0 && (Alerting || DueUtc <= now); }
  public void Snooze(DateTime now) { DueUtc = now.ToUniversalTime().AddMinutes(10).Ticks; Alerting = false; }
  public void Complete() { DueUtc = 0; Alerting = false; }
  public string Label { get { return String.IsNullOrWhiteSpace(Title) ? "未命名便笺" : Title; } }
 }
 [DataContract] public class State {
  [DataMember] public int Version = 1;
  [DataMember] public List<Note> Notes = new List<Note>();
  [DataMember] public string CurrentId;
  [DataMember] public bool Sound = true;
  [DataMember] public double Left = -1, Top = -1, Width = 580, Height = 430;
  [DataMember] public int LayoutVersion = 2;
 }
 public class Store {
  public readonly string Folder;
  public bool Recovered;
  public string LoadNotice;
  public Store(string folder) { Folder = folder; Directory.CreateDirectory(folder); }
  public string FilePath { get { return Path.Combine(Folder, "notes.json"); } }
  static DataContractJsonSerializer Serializer() { return new DataContractJsonSerializer(typeof(State)); }
  public static State Read(string path) {
   using (var s = File.OpenRead(path)) {
    var state = (State)Serializer().ReadObject(s);
    if (state == null || state.Version != 1 || state.Notes == null || state.Notes.Any(n => n == null || String.IsNullOrEmpty(n.Id)) || state.Notes.Select(n => n.Id).Distinct().Count() != state.Notes.Count) throw new InvalidDataException("便笺数据格式无效");
    return state;
   }
  }
  public State Load() {
   if (!File.Exists(FilePath) && !File.Exists(FilePath + ".bak")) return new State();
   try { return Read(FilePath); }
   catch {
    // Preserve damaged data; never replace it with a newly empty state.
    if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".damaged-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"), true);
    try {
     var state = Read(FilePath + ".bak");
     File.Copy(FilePath + ".bak", FilePath, true);
     Recovered = true; LoadNotice = "主数据读取失败，已从上次备份恢复；损坏原件已保留。"; return state;
    } catch { throw new InvalidDataException("便笺和备份均无法读取。原文件已保留，请先备份数据目录再修复：\n" + Folder); }
   }
  }
  public void Save(State state) {
   string temp = FilePath + ".tmp";
   using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { Serializer().WriteObject(stream, state); stream.Flush(true); }
   if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak", true);
   else { File.Move(temp, FilePath); File.Copy(FilePath, FilePath + ".bak", true); }
  }
 }
}
