using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// 311: Gemeinsame Amenity-Konstanten und -Helfer fuer ShopAtJobBoardBays
    /// und DockingBayHUD, damit beide Modul-Komponenten die Bays konsistent
    /// klassifizieren (JobsBoard vs. Shop).
    ///
    /// Statisch gegen die interop Assembly-CSharp.dll verifiziert:
    ///   StationAmenity: None=0, JobsBoard=1, Shop=2, Repairs=3, PaintShop=4,
    ///   BodyShop=5, UpgradeShop=6, ItemDelivery=7, FuelPump=8, ParkingBay=9
    ///   (ilspycmd -t StationAmenity /bepinex/interop/Assembly-CSharp.dll).
    ///   DockingBayGroup ist ein NESTED Typ: Station/DockingBayGroup, mit
    ///   amenityType (StationAmenity) und shopDescription (ShopDescription) als
    ///   Properties; Station.DockingBayGroups -> List&lt;DockingBayGroup&gt;.
    /// </summary>
    public static class AmenityTypes
    {
        public const int JobsBoard = 1; // StationAmenity.JobsBoard (verifiziert)
        public const int Shop = 2;      // StationAmenity.Shop (verifiziert)
    }

    public static class DockingBayAmenityUtil
    {
        /// <summary>
        /// Liest die amenityType-Property eines Il2Cpp-Objekts (DockingBay oder
        /// Station.DockingBayGroup) als int. Basiert auf dem DockingBayHUD-Muster
        /// (direkte Property, Fallback Field).
        /// </summary>
        public static int ReadAmenityType(Il2CppObjectBase obj)
        {
            if (obj == null) return -1;
            try
            {
                var prop = obj.GetType().GetProperty("amenityType", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var val = prop.GetValue(obj);
                    if (val != null) return Convert.ToInt32(val);
                }
                var prop2 = obj.GetType().GetProperty("AmenityType", BindingFlags.Public | BindingFlags.Instance);
                if (prop2 != null)
                {
                    var val2 = prop2.GetValue(obj);
                    if (val2 != null) return Convert.ToInt32(val2);
                }
                var field = obj.GetType().GetField("m_amenityType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    var val3 = field.GetValue(obj);
                    if (val3 != null) return Convert.ToInt32(val3);
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"311b DockingBayAmenityUtil.ReadAmenityType fehlgeschlagen: {ex.Message}");
            }
            return -1;
        }

        /// <summary>
        /// Findet einen Member (Field ODER Property-Getter) per Name ueber den Typ
        /// und alle Basistypen. Il2CppInterop legt manche Il2Cpp-Felder als .NET-
        /// Properties an (z.B. DockingBayGroups, amenityType) - daher beides pruefen.
        /// (Aus DockingBayHUD uebernommen.)
        /// </summary>
        public static MemberInfo FindMember(System.Type t, string name)
        {
            var cur = t;
            while (cur != null && cur != typeof(object))
            {
                var f = cur.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                var p = cur.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (p != null && p.CanRead) return p;
                cur = cur.BaseType;
            }
            // Last resort: case-insensitive scan
            cur = t;
            while (cur != null && cur != typeof(object))
            {
                foreach (var f in cur.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
                foreach (var p in cur.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (p.CanRead && p.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase)) return p;
                cur = cur.BaseType;
            }
            return null;
        }

        public static object GetMemberValue(MemberInfo m, object target)
        {
            if (m is FieldInfo fi) return fi.GetValue(target);
            if (m is PropertyInfo pi) return pi.GetGetMethod(true).Invoke(target, null);
            return null;
        }

        /// <summary>
        /// Robustes Lesen eines Il2Cpp-Felds (native first, dann Property via
        /// Il2CppObjectBase-Ptr, dann Reflection). (Aus DockingBayHUD uebernommen.)
        /// </summary>
        public static unsafe object ReadIl2CppField(MemberInfo member, object target)
        {
            string memberName = member?.Name ?? "?";
            var il2cppObj = target as Il2CppObjectBase;

            // ── Pfad 1: nativer Feld-Lesezugriff via il2cpp_field_get_value ──
            var fi = member as FieldInfo;
            if (fi != null && il2cppObj != null)
            {
                var declType = fi.DeclaringType;
                var nativeField = declType?.GetField("NativeFieldInfoPtr_" + fi.Name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (nativeField != null)
                {
                    try
                    {
                        var nativeFieldPtr = (System.IntPtr)nativeField.GetValue(null);
                        var objPtr = IL2CPP.Il2CppObjectBaseToPtr(il2cppObj);
                        if (objPtr != System.IntPtr.Zero && nativeFieldPtr != System.IntPtr.Zero)
                        {
                            int ptrSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(System.IntPtr));
                            var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(ptrSize);
                            try
                            {
                                IL2CPP.il2cpp_field_get_value(objPtr, nativeFieldPtr, (void*)buf);
                                var fieldValPtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(buf);
                                if (fieldValPtr != System.IntPtr.Zero)
                                {
                                    return System.Activator.CreateInstance(fi.FieldType, new object[] { fieldValPtr });
                                }
                            }
                            finally
                            {
                                System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
                            }
                        }
                    }
                    catch (Exception nex)
                    {
                        StarTruckMP.Log.LogWarning($"311b ReadIl2CppField '{memberName}': native-read failed: {nex.Message}");
                    }
                }
            }

            // ── Pfad 2: Property-Getter via Il2CppObjectBase-Ptr ──
            if (member is PropertyInfo pi && pi.CanRead)
            {
                try
                {
                    var getter = pi.GetGetMethod(true);
                    if (getter != null)
                    {
                        object invokeTarget = il2cppObj != null ? il2cppObj : target;
                        return getter.Invoke(invokeTarget, null);
                    }
                }
                catch (Exception pex)
                {
                    StarTruckMP.Log.LogWarning($"311b ReadIl2CppField '{memberName}': Property-Getter failed: {pex.InnerException?.Message ?? pex.Message}");
                }
            }

            // ── Pfad 3: Reflection GetValue (last resort) ──
            try
            {
                return GetMemberValue(member, target);
            }
            catch (Exception fex)
            {
                StarTruckMP.Log.LogWarning($"311b ReadIl2CppField '{memberName}': ALL paths failed: {fex.Message}");
                return null;
            }
        }

        /// <summary>
        /// True, wenn die Bay in mindestens einer DockingBayGroup mit dem gegebenen
        /// amenityType haengt (Station.m_dockingBayGroups / DockingBayGroups).
        /// </summary>
        public static bool BayInGroupWithAmenity(DockingBay bay, int amenityInt)
        {
            if (bay == null) return false;
            try
            {
                var stationObj = bay.ParentStation;
                if (stationObj == null) return false;
                var m = FindMember(stationObj.GetType(), "DockingBayGroups")
                      ?? FindMember(stationObj.GetType(), "m_dockingBayGroups");
                if (m == null) return false;
                var groups = ReadIl2CppField(m, stationObj) as System.Collections.IEnumerable;
                if (groups == null) return false;
                foreach (var g in groups)
                {
                    var gObj = g as Il2CppObjectBase;
                    if (gObj == null) continue;
                    if (ReadAmenityType(gObj) == amenityInt) return true;
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"311b DockingBayAmenityUtil.BayInGroupWithAmenity fehlgeschlagen: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Bays filtern, die als JobsBoard-Bays klassifiziert werden sollen.
        /// Achtung: amenityType der BAY selbst bleibt JobsBoard (1) - wir schreiben
        /// nur POI/Anzeige um. Diese Methode ist die gemeinsame Klassifikation.
        /// Direkte Property (bay.amenityType), Fallback: Gruppenzugehoerigkeit.
        /// </summary>
        public static bool IsJobsBoardBay(DockingBay bay)
        {
            if (bay == null) return false;
            int amenity = ReadAmenityType(bay);
            if (amenity == AmenityTypes.JobsBoard) return true;
            // Fallback (falls Property unlesbar): Gruppenzugehoerigkeit pruefen
            if (amenity < 0) return BayInGroupWithAmenity(bay, AmenityTypes.JobsBoard);
            return false;
        }
    }
}