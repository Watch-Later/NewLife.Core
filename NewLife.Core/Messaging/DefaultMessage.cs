﻿using NewLife.Data;
using NewLife.Reflection;

namespace NewLife.Messaging;

/// <summary>标准消息SRMP</summary>
/// <remarks>
/// 标准协议最大优势是短小，头部定长，没有序列化成本，适用于专业级RPC以及嵌入式通信。
/// 缺点是可读性差，不能适用于字符串通信场景。
/// 标准网络封包协议：1 Flag + 1 Sequence + 2 Length + N Payload
/// 1个字节标识位，标识请求、响应、错误、加密、压缩等；
/// 1个字节序列号，用于请求响应包配对；
/// 2个字节数据长度N，小端，指示后续负载数据长度（不包含头部4个字节），解决粘包问题；
/// N个字节负载数据，数据内容完全由业务决定，最大长度65535=64k。
/// 如：
/// Open => OK
/// 01-01-04-00-"Open" => 81-01-02-00-"OK"
/// 
/// Length为0xFFFF时，后续4字节为正式长度，以支持超过64k的扩展包
/// </remarks>
public class DefaultMessage : Message
{
    #region 属性
    /// <summary>标记位</summary>
    public Byte Flag { get; set; } = 1;

    /// <summary>序列号，匹配请求和响应</summary>
    public Int32 Sequence { get; set; }

    /// <summary>解析数据时的原始报文</summary>
    private Packet _raw;
    #endregion

    #region 方法
    /// <summary>根据请求创建配对的响应消息</summary>
    /// <returns></returns>
    public override IMessage CreateReply()
    {
        if (Reply) throw new Exception("不能根据响应消息创建响应消息");

        var type = GetType();
        var msg = type == typeof(DefaultMessage) ? new DefaultMessage() : type.CreateInstance() as DefaultMessage;
        msg.Flag = Flag;
        msg.Reply = true;
        msg.Sequence = Sequence;

        return msg;
    }

    /// <summary>从数据包中读取消息</summary>
    /// <param name="pk"></param>
    /// <returns>是否成功</returns>
    public override Boolean Read(Packet pk)
    {
        _raw = pk;

        var count = pk.Total;
        if (count < 4) throw new ArgumentOutOfRangeException(nameof(pk), "数据包头部长度不足4字节");

        // 取头部4个字节
        var size = 4;
        var buf = pk.ReadBytes(0, size);

        // 前2位作为标识位
        Flag = (Byte)(buf[0] & 0b0011_1111);
        var mode = buf[0] >> 6;
        switch (mode)
        {
            case 0: Reply = false; break;
            case 1: OneWay = true; break;
            case 2: Reply = true; break;
            case 3: Reply = true; Error = true; break;
            default:
                break;
        }

        // 1个字节的序列号
        Sequence = buf[1];

        // 负载长度
        var len = (buf[3] << 8) | buf[2];
        if (size + len > count) throw new ArgumentOutOfRangeException(nameof(pk), $"数据包长度{count}不足{size + len}字节");

        // 支持超过64k的超大包
        if (len == 0xFFFF)
        {
            size += 4;
            if (count < size) throw new ArgumentOutOfRangeException(nameof(pk), "数据包头部长度不足8字节");

            len = pk.ReadBytes(size - 4, 4).ToInt();
            if (size + len > count) throw new ArgumentOutOfRangeException(nameof(pk), $"数据包长度{count}不足{size + len}字节");
        }

        // 负载数据
        if (pk.Next == null)
            Payload = new Packet(pk.Data, pk.Offset + size, len);
        else
            Payload = pk.Slice(size, len);

        return true;
    }

    /// <summary>把消息转为封包</summary>
    /// <returns></returns>
    public override Packet ToPacket()
    {
        var pk = Payload;
        var len = 0;
        if (pk != null) len = pk.Total;

        // 增加4字节头部，如果负载数据之前有足够空间则直接使用，否则新建数据包形成链式结构
        var size = len < 0xFFFF ? 4 : 8;
        if (pk.Offset >= size)
            pk = new Packet(pk.Data, pk.Offset - size, pk.Count + size) { Next = pk.Next };
        else
            pk = new Packet(new Byte[size]) { Next = pk };

        // 标记位
        var b = Flag & 0b0011_1111;
        if (Reply) b |= 0x80;
        if (Error || OneWay) b |= 0x40;
        pk[0] = (Byte)b;

        // 序列号
        pk[1] = (Byte)(Sequence & 0xFF);

        if (len < 0xFFFF)
        {
            // 2字节长度，小端字节序
            pk[2] = (Byte)(len & 0xFF);
            pk[3] = (Byte)(len >> 8);
        }
        // 支持64k以上超大包
        else
        {
            pk[2] = 0xFF;
            pk[3] = 0xFF;

            // 再来4字节写长度
            pk.Data.Write((UInt32)len, pk.Offset + 4, true);
        }

        return pk;
    }
    #endregion

    #region 辅助
    /// <summary>获取数据包长度</summary>
    /// <param name="pk"></param>
    /// <returns></returns>
    public static Int32 GetLength(Packet pk)
    {
        if (pk.Total < 4) return 0;

        // 小于64k，直接返回
        var len = pk.Data.ToUInt16(pk.Offset + 2);
        if (len < 0xFFFF) return 4 + len;

        // 超过64k的超大数据包，再来4个字节
        if (pk.Total < 8) return 0;

        return 8 + (Int32)pk.Data.ToUInt32(pk.Offset + 2 + 2);
    }

    /// <summary>获取解析数据时的原始报文</summary>
    /// <returns></returns>
    public Packet GetRaw() => _raw;

    /// <summary>消息摘要</summary>
    /// <returns></returns>
    public override String ToString() => $"{Flag:X2} Seq={Sequence:X2} {Payload}";
    #endregion
}