using EcatMaster.Core.Native;
using Microsoft.Extensions.Logging;

namespace EcatMaster.Core.EtherCat;

/// <summary>
/// PreOp → SafeOp 전환 직전(SOEM PO2SO 훅)에 SDO 로 드라이브를 설정한다.
/// 핵심은 PDO 매핑을 <see cref="AzdPdoMap"/> 대로 고정해 C# 구조체 레이아웃과 일치시키는 것.
/// </summary>
internal static class AzdDriveConfigurator
{
    private const ushort SmRxAssign = 0x1C12;
    private const ushort SmTxAssign = 0x1C13;

    /// <param name="writeOptionCodes">true 면 605Ah/605Dh 를 우리 값으로 덮어쓴다. 셋업이 끝난 장비는 false 로 드라이브 값을 유지.</param>
    public static bool Configure(int slave, bool writeOptionCodes, ILogger log)
    {
        bool ok = true;

        ok &= WritePdoMapping(slave, SmRxAssign, AzdPdoMap.RxPdoIndex, AzdPdoMap.RxEntries, log);
        ok &= WritePdoMapping(slave, SmTxAssign, AzdPdoMap.TxPdoIndex, AzdPdoMap.TxEntries, log);

        if (writeOptionCodes)
        {
            // Halt option code (605Dh): 1 = 감속 램프(6084h)로 정지 후 Operation enabled 유지
            ok &= Check(SoemNative.SdoWrite(slave, 0x605D, 0, (short)1), slave, "605Dh", log);

            // Quick stop option code (605Ah): 2 = quick stop 감속(6085h)으로 정지 후 Switch on disabled
            ok &= Check(SoemNative.SdoWrite(slave, 0x605A, 0, (short)2), slave, "605Ah", log);
        }
        else if (SoemNative.SdoRead(slave, 0x605A, 0, out short qs) && SoemNative.SdoRead(slave, 0x605D, 0, out short ho))
        {
            log.LogInformation("Slave {S}: 드라이브 옵션 코드 유지 (605Ah quick stop = {Qs}, 605Dh halt = {Ho})", slave, qs, ho);
        }

        return ok;
    }

    /// <summary>
    /// 드라이브가 현재 쓰는 PDO 할당(1C12h/1C13h)과 매핑 엔트리를 로그로 출력한다.
    /// 재매핑이 거부됐거나 크기가 안 맞을 때 <see cref="AzdPdoMap"/> 을 드라이브 실제 매핑에 맞추는 데 쓴다.
    /// </summary>
    public static void DumpPdoMapping(int slave, ILogger log)
    {
        DumpAssign(slave, SmRxAssign, "RxPDO (1C12h)", log);
        DumpAssign(slave, SmTxAssign, "TxPDO (1C13h)", log);
    }

    private static void DumpAssign(int slave, ushort smAssign, string label, ILogger log)
    {
        if (!SoemNative.SdoRead(slave, smAssign, 0, out byte count))
        {
            log.LogWarning("Slave {S}: {Label} read failed: {Err}", slave, label, SoemNative.LastError());
            return;
        }
        log.LogInformation("Slave {S}: {Label} assigned PDOs = {N}", slave, label, count);
        for (byte i = 1; i <= count && i <= 4; i++)
        {
            if (!SoemNative.SdoRead(slave, smAssign, i, out ushort pdo)) continue;
            if (!SoemNative.SdoRead(slave, pdo, 0, out byte entries)) continue;
            int bits = 0;
            var sb = new System.Text.StringBuilder();
            for (byte e = 1; e <= entries && e <= 16; e++)
            {
                if (!SoemNative.SdoRead(slave, pdo, e, out uint entry)) continue;
                int len = (int)(entry & 0xFF);
                bits += len;
                sb.Append($"  {(entry >> 16):X4}h:{((entry >> 8) & 0xFF):X2} ({len}bit)");
            }
            log.LogInformation("Slave {S}:   {Pdo:X4}h = {Entries} entries, {Bytes} bytes:{List}", slave, pdo, entries, bits / 8, sb.ToString());
        }
    }

    private static bool WritePdoMapping(int slave, ushort smAssign, ushort pdoIndex, uint[] entries, ILogger log) =>
        PdoMappingWriter.Write(slave, smAssign, pdoIndex, entries, log);

    private static bool Check(bool result, int slave, string what, ILogger log)
    {
        if (!result)
            log.LogError("Slave {Slave}: SDO write {What} failed: {Err}", slave, what, SoemNative.LastError());
        return result;
    }
}
