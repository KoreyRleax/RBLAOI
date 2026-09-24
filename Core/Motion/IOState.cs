using System;

namespace RBLAOI.Core.Motion
{
    /// <summary>
    /// 可设置的 IO 输出信号枚举
    /// </summary>
    public enum OutSignal
    {
        Clamp1,
        Clamp2,
        Block,
        Motor,
        RedLight,
        GreenLight,
        YellowLight,
        Buzzer,
        Camera3DTrigger,
    }

    /// <summary>
    /// 可查询的 IO 输入信号枚举
    /// </summary>
    public enum InputSignal
    {
        WaitSensor,         // InputWait
        WorkSensor,         // InputWork
        LiftUpSensor,       // InputLiftUp
        LiftDownSensor,     // InputLiftDown
        OutputSensor,       // InputOutput
        StartBtn,           // InputStart
        PauseBtn,           // InputPause
        AirPressure,        // InputAirPressure
        Emergency,          // InputEmergency
        RequestMaterial,    // InputRequestMaterial
        SafetyLight,        // InputSafetyLight
        DoorSwitch,         // InputDoorSwitch
    }

    /// <summary>
    /// IO状态类 - 64位（4组16位）
    /// 位0-15:   输入信号
    /// 位16-31:  输出信号
    /// 位32-47:  限位/原点/状态
    /// 位48-63:  预留
    /// </summary>
    public class IOState
    {
        #region ==================== 输入信号 (位 0-15) ====================

        public bool InputWait { get; set; }           // 位0:  X0 待料位感应（入口）
        public bool InputWork { get; set; }           // 位1:  X1 工作位感应（板到位）
        public bool InputLiftUp { get; set; }         // 位2:  X2 顶升上限位
        public bool InputLiftDown { get; set; }       // 位3:  X3 顶升下限位
        public bool InputOutput { get; set; }         // 位4:  X4 出料位感应（出口）
        public bool InputStart { get; set; }          // 位5:  X5 启动
        public bool InputPause { get; set; }          // 位6:  X6 暂停
        public bool InputAirPressure { get; set; }    // 位7:  X7 整机气压检测
        public bool InputEmergency { get; set; }      // 位8:  X8 急停
        public bool InputRequestMaterial { get; set; } // 位9:  X9 后机求料
        public bool InputSafetyLight { get; set; }    // 位10: X10 安全光幕
        public bool InputDoorSwitch { get; set; }     // 位11: X11 门禁开关
        public bool InputReserve12 { get; set; }      // 位12: 预留
        public bool InputReserve13 { get; set; }      // 位13: 预留
        public bool InputReserve14 { get; set; }      // 位14: 预留
        public bool InputReserve15 { get; set; }      // 位15: 预留

        #endregion

        #region ==================== 输出信号 (位 16-31) ====================

        public bool OutputClamp1 { get; set; }          // 位16: Y0 顶升气缸（顶板1）
        public bool OutputClamp2 { get; set; }          // 位17: Y1 顶升气缸（顶板2）
        public bool OutputBlock { get; set; }           // 位18: Y3 出料位阻挡气缸
        public bool OutputMotor { get; set; }           // 位19: Y4 流水线马达
        public bool OutputRedLight { get; set; }        // 位20: Y5 红灯
        public bool OutputGreenLight { get; set; }      // 位21: Y6 绿灯
        public bool OutputYellowLight { get; set; }     // 位22: Y7 黄灯
        public bool OutputBuzzer { get; set; }          // 位23: Y8 蜂鸣器
        public bool OutputCamera3DTrigger { get; set; } // 位24: Y9 3D相机触发
        public bool OutputReserve25 { get; set; }       // 位25: 预留
        public bool OutputReserve26 { get; set; }       // 位26: 预留
        public bool OutputReserve27 { get; set; }       // 位27: 预留
        public bool OutputReserve28 { get; set; }       // 位28: 预留
        public bool OutputReserve29 { get; set; }       // 位29: 预留
        public bool OutputReserve30 { get; set; }       // 位30: 预留
        public bool OutputReserve31 { get; set; }       // 位31: 预留

        #endregion

        #region ==================== 电机板 IO (位 32-63, 布局4: 每轴8位) ====================
        // F401 四轴电机板布局(MOTOR_IO_LAYOUT_AXIS_BYTE_V1)：
        // 综合位 = 32 + 轴序号*8 + 信号偏移（X=0,Y=1,Z=2,H=3）
        // 信号偏移：0=+限位, 1=-限位, 2=原点, 3=报警, 4=驱动器到位(INP), 5=报警清除输出激活, 6-7=预留

        // ===== X 轴（位 32-37）=====
        public bool LimitXPos { get; set; }      // 位32: X正限位
        public bool LimitXNeg { get; set; }      // 位33: X负限位
        public bool OrgX { get; set; }           // 位34: X原点
        public bool AlarmX { get; set; }         // 位35: X轴报警
        public bool AxisXDone { get; set; }      // 位36: X驱动器到位(INP)
        public bool AlarmClearX { get; set; }    // 位37: X报警清除输出激活
        // 位38-39 预留

        // ===== Y 轴（位 40-45）=====
        public bool LimitYPos { get; set; }      // 位40: Y正限位
        public bool LimitYNeg { get; set; }      // 位41: Y负限位
        public bool OrgY { get; set; }           // 位42: Y原点
        public bool AlarmY { get; set; }         // 位43: Y轴报警
        public bool AxisYDone { get; set; }      // 位44: Y驱动器到位(INP)
        public bool AlarmClearY { get; set; }    // 位45: Y报警清除输出激活
        // 位46-47 预留

        // ===== Z 轴（位 48-53）=====
        public bool LimitZPos { get; set; }      // 位48: Z正限位
        public bool LimitZNeg { get; set; }      // 位49: Z负限位
        public bool OrgZ { get; set; }           // 位50: Z原点
        public bool AlarmZ { get; set; }         // 位51: Z轴报警
        public bool AxisZDone { get; set; }      // 位52: Z驱动器到位(INP)
        public bool AlarmClearZ { get; set; }    // 位53: Z报警清除输出激活
        // 位54-55 预留

        // ===== H 轴（位 56-61）=====
        public bool LimitHPos { get; set; }      // 位56: H正限位
        public bool LimitHNeg { get; set; }      // 位57: H负限位
        public bool OrgH { get; set; }           // 位58: H原点
        public bool AlarmH { get; set; }         // 位59: H轴报警
        public bool AxisHDone { get; set; }      // 位60: H驱动器到位(INP)
        public bool AlarmClearH { get; set; }    // 位61: H报警清除输出激活
        // 位62-63 预留

        #endregion

        #region ==================== 便捷属性 ====================

        /// <summary>
        /// 任一限位触发（四轴正负限位）
        /// </summary>
        public bool HasLimitTrigger => LimitXPos || LimitXNeg || LimitYPos || LimitYNeg || LimitZPos || LimitZNeg || LimitHPos || LimitHNeg;

        /// <summary>
        /// 急停状态（F401 电机板无急停位，急停由主板本地 IO 位8 承载）
        /// </summary>
        public bool IsEmergencyStop => InputEmergency;

        /// <summary>
        /// 安全状态（安全光幕未触发 + 门禁正常 + 气压正常）
        /// </summary>
        public bool IsSafe => !InputSafetyLight && !InputDoorSwitch && InputAirPressure;

        /// <summary>
        /// 任意轴报警（四轴）
        /// </summary>
        public bool HasAlarm => AlarmX || AlarmY || AlarmZ || AlarmH;

        /// <summary>
        /// X/Y/Z/H 四轴驱动器到位（INP）——仅表示实时到位输入，与软件运动完成事件(X_COMPLETE 等)不同义
        /// </summary>
        public bool AllAxesDone => AxisXDone && AxisYDone && AxisZDone && AxisHDone;

        #endregion

        #region ==================== 解析方法 ====================

        /// <summary>
        /// 当前电机 IO 布局版本（MOTOR_IO_LAYOUT）：
        /// 4 = MOTOR_IO_LAYOUT_AXIS_BYTE_V1（F401 四轴板，每轴 8 位，默认）；
        /// 1/2 = 旧 F103 双板布局（回归用）。接入 GET_CONFIG 后按 io_layout 字段设置。
        /// 未知布局必须提示不兼容，不能猜测位序。
        /// </summary>
        public static int IoLayout { get; set; } = 4;

        /// <summary>
        /// 从64位十六进制解析IO状态（16位十六进制字符串），按 IoLayout 分发到对应解析器
        /// </summary>
        public static IOState ParseFromHex64(string hexData)
        {
            var state = new IOState();

            string content = hexData;
            if (hexData.StartsWith("IO:"))
                content = hexData.Substring(3);
            content = content.Trim();

            // 去掉可能的 0x 前缀
            if (content.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                content = content.Substring(2);

            // 使用 64 位整数解析
            if (ulong.TryParse(content, System.Globalization.NumberStyles.HexNumber, null, out ulong value))
            {
                ParseInputOutputBits(value, state);   // 位 0-31：主板本地 IO，两种布局相同

                if (IoLayout == 4)
                    ParseMotorBitsAxisByte(value, state);  // 布局4：F401 每轴 8 位
                else
                    ParseMotorBitsLegacy(value, state);    // 旧 F103 双板布局（回归）
            }

            return state;
        }

        /// <summary>位 0-31：主板本地 IO（输入传感器 + 输出执行器），所有布局通用</summary>
        private static void ParseInputOutputBits(ulong value, IOState state)
        {
            // ==================== 输入信号 (位 0-15) ====================
            state.InputWait = (value & 0x0000000000000001) != 0;
            state.InputWork = (value & 0x0000000000000002) != 0;
            state.InputLiftUp = (value & 0x0000000000000004) != 0;
            state.InputLiftDown = (value & 0x0000000000000008) != 0;
            state.InputOutput = (value & 0x0000000000000010) != 0;
            state.InputStart = (value & 0x0000000000000020) != 0;
            state.InputPause = (value & 0x0000000000000040) != 0;
            state.InputAirPressure = (value & 0x0000000000000080) != 0;
            state.InputEmergency = (value & 0x0000000000000100) != 0;
            state.InputRequestMaterial = (value & 0x0000000000000200) != 0;
            state.InputSafetyLight = (value & 0x0000000000000400) != 0;
            state.InputDoorSwitch = (value & 0x0000000000000800) != 0;
            state.InputReserve12 = (value & 0x0000000000001000) != 0;
            state.InputReserve13 = (value & 0x0000000000002000) != 0;
            state.InputReserve14 = (value & 0x0000000000004000) != 0;
            state.InputReserve15 = (value & 0x0000000000008000) != 0;

            // ==================== 输出信号 (位 16-31) ====================
            state.OutputClamp1 = (value & 0x0000000000010000) != 0;
            state.OutputClamp2 = (value & 0x0000000000020000) != 0;
            state.OutputBlock = (value & 0x0000000000040000) != 0;
            state.OutputMotor = (value & 0x0000000000080000) != 0;
            state.OutputRedLight = (value & 0x0000000000100000) != 0;
            state.OutputGreenLight = (value & 0x0000000000200000) != 0;
            state.OutputYellowLight = (value & 0x0000000000400000) != 0;
            state.OutputBuzzer = (value & 0x0000000000800000) != 0;
            state.OutputCamera3DTrigger = (value & 0x0000000001000000) != 0;
            state.OutputReserve25 = (value & 0x0000000002000000) != 0;
            state.OutputReserve26 = (value & 0x0000000004000000) != 0;
            state.OutputReserve27 = (value & 0x0000000008000000) != 0;
            state.OutputReserve28 = (value & 0x0000000010000000) != 0;
            state.OutputReserve29 = (value & 0x0000000020000000) != 0;
            state.OutputReserve30 = (value & 0x0000000040000000) != 0;
            state.OutputReserve31 = (value & 0x0000000080000000) != 0;
        }

        /// <summary>
        /// 位 32-63：F401 四轴板布局（布局4，每轴 8 位）。
        /// 综合位 = 32 + 轴序号*8 + 信号偏移；信号偏移：0=+限位,1=-限位,2=原点,3=报警,4=到位(INP),5=报警清除,6-7=预留
        /// </summary>
        private static void ParseMotorBitsAxisByte(ulong value, IOState state)
        {
            // ===== X 轴（位 32-37）=====
            state.LimitXPos = (value & 0x0000000100000000) != 0;   // 32
            state.LimitXNeg = (value & 0x0000000200000000) != 0;   // 33
            state.OrgX = (value & 0x0000000400000000) != 0;        // 34
            state.AlarmX = (value & 0x0000000800000000) != 0;      // 35
            state.AxisXDone = (value & 0x0000001000000000) != 0;   // 36
            state.AlarmClearX = (value & 0x0000002000000000) != 0; // 37

            // ===== Y 轴（位 40-45）=====
            state.LimitYPos = (value & 0x0000010000000000) != 0;   // 40
            state.LimitYNeg = (value & 0x0000020000000000) != 0;   // 41
            state.OrgY = (value & 0x0000040000000000) != 0;        // 42
            state.AlarmY = (value & 0x0000080000000000) != 0;      // 43
            state.AxisYDone = (value & 0x0000100000000000) != 0;   // 44
            state.AlarmClearY = (value & 0x0000200000000000) != 0; // 45

            // ===== Z 轴（位 48-53）=====
            state.LimitZPos = (value & 0x0001000000000000) != 0;   // 48
            state.LimitZNeg = (value & 0x0002000000000000) != 0;   // 49
            state.OrgZ = (value & 0x0004000000000000) != 0;        // 50
            state.AlarmZ = (value & 0x0008000000000000) != 0;      // 51
            state.AxisZDone = (value & 0x0010000000000000) != 0;   // 52
            state.AlarmClearZ = (value & 0x0020000000000000) != 0; // 53

            // ===== H 轴（位 56-61）=====
            state.LimitHPos = (value & 0x0100000000000000) != 0;   // 56
            state.LimitHNeg = (value & 0x0200000000000000) != 0;   // 57
            state.OrgH = (value & 0x0400000000000000) != 0;        // 58
            state.AlarmH = (value & 0x0800000000000000) != 0;      // 59
            state.AxisHDone = (value & 0x1000000000000000) != 0;   // 60
            state.AlarmClearH = (value & 0x2000000000000000) != 0; // 61

            // 位 38-39, 46-47, 54-55, 62-63 预留（F401 恒为 0，忽略）
        }

        /// <summary>位 32-63：旧 F103 XYZ 三轴板 + F103 H 单轴板布局（布局 1/2，仅回归用）</summary>
        private static void ParseMotorBitsLegacy(ulong value, IOState state)
        {
            state.LimitXPos = (value & 0x0000000100000000) != 0;
            state.LimitXNeg = (value & 0x0000000200000000) != 0;
            state.OrgX = (value & 0x0000000400000000) != 0;
            state.LimitYPos = (value & 0x0000000800000000) != 0;
            state.LimitYNeg = (value & 0x0000001000000000) != 0;
            state.OrgY = (value & 0x0000002000000000) != 0;
            state.LimitZPos = (value & 0x0000004000000000) != 0;
            state.LimitZNeg = (value & 0x0000008000000000) != 0;
            state.OrgZ = (value & 0x0000010000000000) != 0;
            state.AlarmX = (value & 0x0000020000000000) != 0;
            state.AlarmY = (value & 0x0000040000000000) != 0;
            state.AlarmZ = (value & 0x0000080000000000) != 0;
            state.AxisXDone = (value & 0x0000200000000000) != 0;
            state.AxisYDone = (value & 0x0000400000000000) != 0;
            state.AxisZDone = (value & 0x0000800000000000) != 0;
            state.AlarmClearX = (value & 0x0001000000000000) != 0;
            state.AlarmClearY = (value & 0x0002000000000000) != 0;
            state.AlarmClearZ = (value & 0x0004000000000000) != 0;
            // 旧布局 H 板 IO 位于高 16 位（H 限位/原点），Legacy 回归时忽略；急停位 44 已废弃
        }

        /// <summary>
        /// 转换为64位十六进制（16位十六进制字符串）
        /// </summary>
        public string ToHexString64()
        {
            ulong value = 0;

            // 输入信号
            if (InputWait) value |= 0x0000000000000001;
            if (InputWork) value |= 0x0000000000000002;
            if (InputLiftUp) value |= 0x0000000000000004;
            if (InputLiftDown) value |= 0x0000000000000008;
            if (InputOutput) value |= 0x0000000000000010;
            if (InputStart) value |= 0x0000000000000020;
            if (InputPause) value |= 0x0000000000000040;
            if (InputAirPressure) value |= 0x0000000000000080;
            if (InputEmergency) value |= 0x0000000000000100;
            if (InputRequestMaterial) value |= 0x0000000000000200;
            if (InputSafetyLight) value |= 0x0000000000000400;
            if (InputDoorSwitch) value |= 0x0000000000000800;
            if (InputReserve12) value |= 0x0000000000001000;
            if (InputReserve13) value |= 0x0000000000002000;
            if (InputReserve14) value |= 0x0000000000004000;
            if (InputReserve15) value |= 0x0000000000008000;

            // 输出信号
            if (OutputClamp1) value |= 0x0000000000010000;
            if (OutputClamp2) value |= 0x0000000000020000;
            if (OutputBlock) value |= 0x0000000000040000;
            if (OutputMotor) value |= 0x0000000000080000;
            if (OutputRedLight) value |= 0x0000000000100000;
            if (OutputGreenLight) value |= 0x0000000000200000;
            if (OutputYellowLight) value |= 0x0000000000400000;
            if (OutputBuzzer) value |= 0x0000000000800000;
            if (OutputCamera3DTrigger) value |= 0x0000000001000000;
            if (OutputReserve25) value |= 0x0000000002000000;
            if (OutputReserve26) value |= 0x0000000004000000;
            if (OutputReserve27) value |= 0x0000000008000000;
            if (OutputReserve28) value |= 0x0000000010000000;
            if (OutputReserve29) value |= 0x0000000020000000;
            if (OutputReserve30) value |= 0x0000000040000000;
            if (OutputReserve31) value |= 0x0000000080000000;

            // 电机板 IO（位 32-63，布局4：每轴 8 位）
            if (LimitXPos) value |= 0x0000000100000000;      // 32
            if (LimitXNeg) value |= 0x0000000200000000;      // 33
            if (OrgX) value |= 0x0000000400000000;           // 34
            if (AlarmX) value |= 0x0000000800000000;         // 35
            if (AxisXDone) value |= 0x0000001000000000;      // 36
            if (AlarmClearX) value |= 0x0000002000000000;    // 37

            if (LimitYPos) value |= 0x0000010000000000;      // 40
            if (LimitYNeg) value |= 0x0000020000000000;      // 41
            if (OrgY) value |= 0x0000040000000000;           // 42
            if (AlarmY) value |= 0x0000080000000000;         // 43
            if (AxisYDone) value |= 0x0000100000000000;      // 44
            if (AlarmClearY) value |= 0x0000200000000000;    // 45

            if (LimitZPos) value |= 0x0001000000000000;      // 48
            if (LimitZNeg) value |= 0x0002000000000000;      // 49
            if (OrgZ) value |= 0x0004000000000000;           // 50
            if (AlarmZ) value |= 0x0008000000000000;         // 51
            if (AxisZDone) value |= 0x0010000000000000;      // 52
            if (AlarmClearZ) value |= 0x0020000000000000;    // 53

            if (LimitHPos) value |= 0x0100000000000000;      // 56
            if (LimitHNeg) value |= 0x0200000000000000;      // 57
            if (OrgH) value |= 0x0400000000000000;           // 58
            if (AlarmH) value |= 0x0800000000000000;         // 59
            if (AxisHDone) value |= 0x1000000000000000;      // 60
            if (AlarmClearH) value |= 0x2000000000000000;    // 61

            return value.ToString("X16");
        }

        #endregion
    }
}