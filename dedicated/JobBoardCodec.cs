using System;
using System.Collections.Generic;
using StarTruckMP.Common;

namespace StarTruckMP.Dedicated;

/// <summary>
/// Wire-Codecs fuer den server-authoritativen Job-Sync (custom-build-342).
/// Explizite Binaer-Serialisierung (BinaryWriter-Prinzip wie Client/CargoSync.cs) —
/// bewusst KEINE FlatSharp-Unions (IL2CPP-'Discriminator=0'-Lesson).
///
/// Upload-Wireformat (Client->Server, chunked vom Client, hier nach Assemblierung):
///   [int jobCount] { je Job: [string questId][string displayName][string displayDescription]
///     [int paramCount] { [string name][byte kind][string strValue] } }
/// Download-/Pool-Nachricht (Server->Client, identisches Job-Format + [int poolVersion]).
/// </summary>
public static class JobBoardCodec
{
    public static List<JobBoardJob> DecodeJobs(ReadOnlySpan<byte> payload)
    {
        var jobs = new List<JobBoardJob>();
        int off = 0;
        int jobCount = ReadInt(payload, ref off);
        for (int j = 0; j < jobCount; j++)
        {
            var job = new JobBoardJob
            {
                QuestId = ReadString(payload, ref off),
                DisplayName = ReadString(payload, ref off),
                DisplayDescription = ReadString(payload, ref off),
            };
            int paramCount = ReadInt(payload, ref off);
            job.Params = new List<JobBoardParam>(Math.Max(paramCount, 0));
            for (int p = 0; p < paramCount; p++)
            {
                job.Params.Add(new JobBoardParam
                {
                    Name = ReadString(payload, ref off),
                    Kind = payload[off++],
                    StrValue = ReadString(payload, ref off),
                });
            }
            jobs.Add(job);
        }
        return jobs;
    }

    public static byte[] EncodeJobs(IReadOnlyList<JobBoardJob> jobs, int poolVersion)
    {
        int size = 4 + 4;
        foreach (var job in jobs) size += job.WireSize();
        var buf = new byte[size];
        int off = 0;
        WriteInt(buf, ref off, jobs.Count);
        WriteInt(buf, ref off, poolVersion);
        foreach (var job in jobs)
        {
            WriteString(buf, ref off, job.QuestId);
            WriteString(buf, ref off, job.DisplayName);
            WriteString(buf, ref off, job.DisplayDescription);
            WriteInt(buf, ref off, job.Params?.Count ?? 0);
            if (job.Params != null)
                foreach (var p in job.Params)
                {
                    WriteString(buf, ref off, p.Name);
                    buf[off++] = p.Kind;
                    WriteString(buf, ref off, p.StrValue);
                }
        }
        if (off != size) throw new InvalidOperationException($"EncodeJobs size mismatch: {off} != {size}");
        return buf;
    }

    public static (List<JobBoardJob> jobs, int poolVersion) DecodePool(ReadOnlySpan<byte> payload)
    {
        int off = 0;
        int jobCount = ReadInt(payload, ref off);
        int poolVersion = ReadInt(payload, ref off);
        var jobs = new List<JobBoardJob>(Math.Max(jobCount, 0));
        for (int j = 0; j < jobCount; j++)
        {
            var job = new JobBoardJob
            {
                QuestId = ReadString(payload, ref off),
                DisplayName = ReadString(payload, ref off),
                DisplayDescription = ReadString(payload, ref off),
            };
            int paramCount = ReadInt(payload, ref off);
            job.Params = new List<JobBoardParam>(Math.Max(paramCount, 0));
            for (int p = 0; p < paramCount; p++)
            {
                job.Params.Add(new JobBoardParam
                {
                    Name = ReadString(payload, ref off),
                    Kind = payload[off++],
                    StrValue = ReadString(payload, ref off),
                });
            }
            jobs.Add(job);
        }
        return (jobs, poolVersion);
    }

    // ---- little-endian primitives ----
    private static int ReadInt(ReadOnlySpan<byte> b, ref int off)
    {
        int v = b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24);
        off += 4;
        return v;
    }

    private static void WriteInt(byte[] b, ref int off, int v)
    {
        b[off++] = (byte)v; b[off++] = (byte)(v >> 8); b[off++] = (byte)(v >> 16); b[off++] = (byte)(v >> 24);
    }

    private static string ReadString(ReadOnlySpan<byte> b, ref int off)
    {
        int len = ReadInt(b, ref off);
        if (len <= 0) return "";
        var s = System.Text.Encoding.UTF8.GetString(b.Slice(off, len));
        off += len;
        return s;
    }

    private static void WriteString(byte[] b, ref int off, string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s ?? "");
        WriteInt(b, ref off, bytes.Length);
        bytes.CopyTo(b, off);
        off += bytes.Length;
    }
}
