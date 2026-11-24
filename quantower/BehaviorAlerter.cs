// BehaviorAlerter — поведенческий алертер для Quantower. Сборка 15, 24.11.2026.
//
// Следит за собственными исполнениями трейдера и сообщает звуком о паттернах,
// которые обычно предшествуют плохому дню. Торговой логики нет: модуль ничего
// не блокирует, не закрывает и не выставляет ордеров — только сигналит и пишет журнал.
//
// Алерты:
//   A1  вход менее чем через N сек после закрытия убыточной сделки       -> ПАУЗА
//   A2  серия убытков подряд, номер серии слышен и в тексте              -> ПАУЗА + отсчёт
//   A3  позиция без стопа                                                -> НАРУШЕНИЕ
//   размер вырос после окна формирования входа                           -> НАРУШЕНИЕ
//   A5b размер входа выше базового после убытка                          -> статистика, без звука
//   порог счётчика состояния достигнут  -> ЛИМИТ, один раз, отдельный Core.Alert
//   рубеж сессии от первого фила        -> ЛИМИТ, если продление не заработано
//
// Счётчик состояния — единственное, что модуль измеряет. Накопительный счёт
// четырёх типов событий за сессию: быстрый перезаход, вход внутри отсчёта,
// позиция без стопа, рост размера. Ничего не блокирует, только считает
// и сообщает о переходе порога.
//
// Не считает эквити и не выводит дневной результат на экран. Таймера сессии
// по часам нет намеренно: длительность зависит от контекста дня.
// session_elapsed_sec пишется в лог, чтобы правило можно было вывести
// из данных, а не назначить заранее.
//
// ФАЙЛЫ (по умолчанию Документы\TradingBehaviorLog)
//   behavior_YYYYMMDD.jsonl  — журнал событий, одна JSON-строка на событие
//   state_YYYYMMDD.txt       — состояние сессии, переживает перезапуск модуля
//   pause.wav, violation.wav, limit.wav — звуки; свой файл кладётся вместо
//                              сгенерированного и не перезаписывается
//
// ---------------------------------------------------------------------------
// КЛЮЧЕВЫЕ РЕШЕНИЯ
//
//   Состояние по переходам, а не по событиям платформы. Платформа может
//   переподнимать PositionAdded на каждом обновлении позиции, поэтому вход,
//   добор, сокращение и выход определяются по знакам нетто-позиции,
//   накопленной по филам (см. раздел «филы»). PositionImpactType только пишется.
//
//   Выход подтверждается платформой. Филы сказали «ноль» — модуль ждёт
//   ExitConfirmSec и сверяется с Core.Instance.Positions. Позиция на месте —
//   это расхождение, а не выход: счётчики не трогаются, нетто чинится,
//   пишется module_error.
//
//   Одно действие не читается как два. Повторный фил с тем же Id игнорируется,
//   разворот одной заявкой не даёт A1, перезаход быстрее MinReentrySec
//   считается дефектом модуля. Всё подавленное пишется в лог: тише становится
//   только звук, не данные.
//
//   Убыток / скрэтч / прибыль решает одна функция ClassifyResult: валовый
//   результат в тиках на контракт с полосой скрэтча вокруг нуля.
//
//   Вход формируется в окне EntryWindowSec от первого фила. Доборы внутри окна —
//   часть входа, рост после окна — событие size_increase.
//
//   Обработчик филов возвращается немедленно. TradeAdded приходит, судя по
//   поведению, в потоке интерфейса, поэтому диск, звук, Strategy.Log
//   и Core.Instance.Alert уходят в один фоновый поток с очередью. Панель
//   читает готовый снимок без блокировки, звуки генерируются в фоне
//   и прогреваются в кеш при старте.
//
//   Core.Instance.Alert для рядовых алертов выключен по умолчанию: это
//   единственный вызов, которым модуль обращается к платформе в момент
//   срабатывания. Безусловно подаётся только сигнал порога, один раз за сессию.
//   Если в момент сигнала в терминале появятся необъяснимые эффекты,
//   проверять этот вызов первым.
//
// ИСТОРИЯ
//   Сборка 15 (24.11.2026) — публичная версия. ts_local и время рубежа берутся
//     из часового пояса Windows вместо параметра смещения. Комментарии очищены.
//   Сборки 1–14 (08–09.2026) — внутренняя разработка; выводы сведены выше.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TradingPlatform.BusinessLayer;

namespace BehaviorAlerter
{
    public class BehaviorAlerter : Strategy
    {
        // ------------------------------------------------------------------ параметры

        [InputParameter("Символ (пусто = любой)", 10)]
        public Symbol Symbol;

        [InputParameter("Базовый размер (только для лога)", 20, 1, 100, 1, 0)]
        public int BaseSize = 1;

        [InputParameter("A1: окно быстрого перезахода, сек", 30, 1, 600, 1, 0)]
        public int FastReentrySec = 60;

        // Человек не перезаходит за доли секунды. Интервал ниже порога означает
        // дефект модуля (одно действие, прочитанное дважды), а не поведение трейдера.
        [InputParameter("A1: интервал ниже этого — дефект, сек", 31, 0, 30, 1, 1)]
        public double MinReentrySec = 5.0;

        [InputParameter("Подтверждение выхода платформой, сек", 66, 0, 10, 1, 1)]
        public double ExitConfirmSec = 1.5;

        // Размер входа определяется агрегацией филлов, поэтому A5b ждёт,
        // пока входная заявка дольётся.
        [InputParameter("Сбор размера входа, сек", 67, 0, 30, 1, 1)]
        public double EntrySettleSec = 2.0;

        // Рубеж сессии (в журнале — rule3): DeadlineMinutes от ПЕРВОГО ФИЛА дня.
        // Без торговли в премаркете первый фил дня совпадает с первым после открытия.
        [InputParameter("Рубеж сессии от первого фила, мин", 71, 10, 1440, 1, 0)]
        public int DeadlineMinutes = 120;

        // Продление разрешено, если ЗАКРЫТАЯ прибыль на рубеже выше порога.
        // Порог подбирается под правила своего счёта.
        [InputParameter("Порог продления, закрытая прибыль", 72, 0, 100000, 1, 2)]
        public double ExtensionProfit = 450;

        // Порог счётчика состояния. Ориентир, подбирается по своим данным.
        [InputParameter("Порог счётчика состояния", 68, 1, 500, 1, 0)]
        public int StateThreshold = 20;

        // Окно формирования входа. Рост размера внутри окна от первого фила —
        // часть входа, событие не порождает; рост после окна — size_increase.
        // Пять секунд подобраны по распределению набора позиции по филлам:
        // большинство многофильных входов укладывается в это окно.
        // Параметр, а не константа. Отрицательное значение отключает событие.
        [InputParameter("Окно формирования входа, сек", 69, -1, 600, 1, 1)]
        public double EntryWindowSec = 5.0;

        [InputParameter("A2: длительность отсчёта, сек", 40, 10, 900, 1, 0)]
        public int PauseSec = 120;

        // Полоса нечувствительности, тиков НА КОНТРАКТ. В долларах её задавать нельзя:
        // на размере 10 та же сумма превратилась бы в один тик.
        [InputParameter("Полоса скрэтча, тиков на контракт", 42, 0, 50, 1, 0)]
        public int ScratchBandTicks = 2;

        [InputParameter("Стоимость тика, $ за контракт", 43, 0.01, 1000, 2, 2)]
        public double TickValueUsd = 0.50;

        [InputParameter("A3: молчать, если окно короче, сек", 50, 1, 300, 1, 0)]
        public int StopQuietSec = 15;

        [InputParameter("A3: звук, если окно длиннее, сек", 60, 15, 900, 1, 0)]
        public int StopLoudSec = 60;

        [InputParameter("Задержка фиксации выхода, сек", 65, 1, 30, 1, 0)]
        public int ExitSettleSec = 2;

        [InputParameter("Папка лога (пусто = Документы)", 80)]
        public string LogFolder = "";

        // ВЫКЛЮЧЕНО ПО УМОЛЧАНИЮ. Это единственный вызов, которым модуль обращается
        // к платформе в момент срабатывания. Алерты Quantower — штатная подсистема,
        // к которой в настройках можно привязать действие. Включать сознательно.
        [InputParameter("Дублировать в алерты Quantower", 110)]
        public bool UsePlatformAlert = false;

        // Strategy.Log пишет в журнал платформы. Он вызывается на фоновом потоке,
        // но если панель журнала тормозит, очередь записи на диск встаёт за ним.
        // Выключается, если фризы вернутся и подозрение падёт сюда.
        [InputParameter("Дублировать в журнал платформы", 111)]
        public bool MirrorToPlatformLog = true;

        // ------------------------------------------------------------------ состояние

        // не Version: так называется член базового класса Strategy, платформа читает свой
        public const string ModuleVersion = "сборка 15 от 24.11.2026";

        // Имя сигнала порога обязано отличаться от рядового. Рядовых за сессию
        // бывают сотни, и сигнал, неотличимый от них, бесполезен по построению.
        private const string AlertNameRoutine = "BehaviorAlerter";
        private const string AlertNameSessionEnd = "BehaviorAlerter.ЗАВЕРШИТЬ СЕССИЮ";

        private readonly object sync = new object();
        private readonly object ioLock = new object();   // только файл лога, отдельно от состояния
        private StreamWriter writer;
        private string folder;
        private string statePath;
        private string wavPause, wavViolation, wavLimit;
        private Timer heartbeat;

        private int consecutiveLosses;
        private int tradesCount;
        private DateTime lastLossExitUtc = DateTime.MinValue;
        private DateTime lastExitUtc = DateTime.MinValue;
        private DateTime sessionAnchorUtc = DateTime.MinValue;
        private DateTime pauseStartedUtc = DateTime.MinValue;
        private DateTime pauseUntilUtc = DateTime.MinValue;
        private string lastEventText = "-";

        // знаковая нетто-позиция, накопленная по филам. Единственный источник
        // классификации входа/добора/сокращения/выхода.
        private double netQty;

        // защита от дублей: платформа может переподнимать события (PositionAdded раз в несколько секунд)
        private readonly HashSet<string> seenTradeIds = new HashSet<string>();
        private string lastExitOrderId = "";
        private int netMismatchTicks;

        // Выход, объявленный филами, но ещё не подтверждённый платформой.
        private bool exitPending;
        private DateTime exitPendingAtUtc = DateTime.MinValue;
        private string exitPendingSource = "";

        // Счётчик состояния. Накопительный за сессию, переживает перезапуск.
        private int stateCounter;
        private int cntFastReentry, cntBypass, cntNoStop, cntSizeGrowth;
        private bool thresholdFired;

        // Рубеж, решение о продлении и признак, что рубеж уже отработан.
        private bool deadlineFired;
        private bool extensionAllowed;

        // Накопленный результат сессии, внутридневной пик и просадка от него.
        // В лог — да, на панель — нет: крупное постоянное число PnL на экране
        // тянет торговать на результат, а не по плану.
        private double sessionNet, peakNet, ddFromPeak;

        private int consecutiveWins;
        private int lastCloseKind;                               // -1 убыток, 0 скрэтч, 1 прибыль
        private double lastCloseTicks;                           // результат прошлого круга, тиков на контракт

        private DateTime lastStreakIncUtc = DateTime.MinValue;   // защита A2 от двойного инкремента
        private string lastEventType = "-";                      // для диагностики в module_error

        // Панель Strategy Runner рисуется потоком интерфейса. Если бы OnGetMetrics брал
        // sync, интерфейс терминала ждал бы окончания записи на диск — а в момент A2
        // подряд идут три обращения к файлам. Поэтому панель читает готовый снимок
        // без блокировки: присваивание ссылки атомарно.
        private sealed class Snap
        {
            public int PauseLeft, NoStopSec, Streak, Wins, Trades, SessionMin, State;
            public string Last = "-";
        }
        private volatile Snap snap = new Snap();

        private volatile bool stateDirty;

        // Всё, что может подвиснуть — запись на диск, PlaySound, Strategy.Log — уходит сюда.
        // TradeAdded от платформы приходит, судя по всему, в потоке интерфейса, поэтому
        // обработчик филов обязан возвращаться немедленно. Порядок сохраняется: поток один.
        private readonly Queue<Action> jobs = new Queue<Action>();
        private readonly AutoResetEvent jobSignal = new AutoResetEvent(false);
        private Thread worker;
        private volatile bool running;

        // расписание событий A3 от начала окна, сек
        private static readonly int[] StopSchedule = { 15, 30, 45, 60, 180, 300 };
        private const int StopScheduleTail = 300; // дальше каждые 300

        private class Track
        {
            public string PositionKey = "";
            public string TradeId = "";           // OrderId входной заявки
            public string SymbolName = "";
            public string EntrySource = "";       // fill | snapshot
            public string EntryImpact = "";       // сырое значение PositionImpactType
            public DateTime OpenedUtc;
            public DateTime ExitDetectedUtc = DateTime.MinValue;

            public double EntrySize, Size, MaxSize;

            public bool HasStop;
            public double StopDistanceTicks = -1;
            public double PnlTicks;
            public double SnapshotNetPnl;

            // A3
            public bool StopWasPresent;
            public DateTime StopMissingSinceUtc = DateTime.MinValue;
            public int StopScheduleIdx;
            public bool StopSoundFired;

            // Доборы, уже объявленные, чтобы сплит филлов одной заявки не считался добором
            public readonly HashSet<string> ReportedAddOrders = new HashSet<string>();

            // Агрегация филлов входной заявки. Заявка на 10, разбитая на десять
            // филлов, — это вход размером 10, а не десять входов.
            public double EntryOrderQty;
            public DateTime FirstFillUtc;          // якорь окна формирования входа
            public DateTime LastEntryFillUtc;      // от последнего фила входа до стопа

            // Событие entry придерживается до появления стопа, чтобы нести
            // фактический зазор. Немедленный след даёт entry_pending.
            public bool EntryEventPending;
            public double StopGapSec = -1;

            // Вид окна без стопа — явное поле, а не текст
            public bool StopEverSeen;

            // Вход случился после рубежа, продление не было разрешено
            public bool AfterDeadlineNoExtension;

            // Сколькими заявками набирался вход и за какой срок. По правилу окна всё, что попало
            // в окно, — один вход, но факт «три заявки за две секунды» из данных
            // теряться не должен: на счётчик не влияет, для статистики считается.
            public readonly HashSet<string> EntryOrders = new HashSet<string>();
            public bool EntryEvalPending;
            public DateTime EntryEvalAtUtc = DateTime.MinValue;
            public int PrevCloseKind;          // классификация непосредственно предыдущего закрытия
            public double PrevCloseTicks;
            public int WinsAtOpen;

            public double RealizedGross, RealizedNet, RealizedFee;
            public int Fills;
            public int PartialExits;
        }

        private Track active;                    // одна открытая позиция за раз (неттинг)
        private static readonly TimeZoneInfo NyTz = ResolveNyTimeZone();

        public BehaviorAlerter() : base()
        {
            this.Name = "Behavior Alerter";
            this.Description = "Поведенческие алерты исполнения: A1, A2, A3, N1. " + ModuleVersion;
        }

        // ------------------------------------------------------------------ жизненный цикл

        protected override void OnRun()
        {
            try
            {
                this.folder = ResolveFolder(this.LogFolder);
                Directory.CreateDirectory(this.folder);

                this.wavPause = Path.Combine(this.folder, "pause.wav");
                this.wavViolation = Path.Combine(this.folder, "violation.wav");
                this.wavLimit = Path.Combine(this.folder, "limit.wav");

                var nowUtc = DateTime.UtcNow;
                var dayKey = ToNy(nowUtc).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

                var logPath = Path.Combine(this.folder, "behavior_" + dayKey + ".jsonl");
                this.statePath = Path.Combine(this.folder, "state_" + dayKey + ".txt");

                this.writer = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                this.writer.AutoFlush = true;

                this.running = true;
                this.worker = new Thread(WorkerLoop);
                this.worker.IsBackground = true;
                this.worker.Name = "BehaviorAlerter.io";
                this.worker.Start();

                // Генерация звуков — на фоновый поток. Первый запуск считает под миллион
                // отсчётов синуса и пишет около мегабайта: в потоке запуска стратегии
                // этому делать нечего.
                Enqueue(EnsureSounds);

                RestoreState();

                var minutesTo930 = MinutesToNyOpen(nowUtc);
                var selfCheck = string.Format(CultureInfo.InvariantCulture,
                    "папка={0} | звуки пауза={1} серия={2} нарушение={3} лимит={4}" +
                    " | порог счётчика {5} | окно формирования входа {6}" +
                    " | рубеж {7} мин, порог продления {8} | рубеж дня {9}" +
                    " | до 9:30 NY {10} мин",
                    this.folder,
                    SoundState(this.wavPause),
                    SoundState(PauseFileFor(2)),
                    SoundState(this.wavViolation),
                    SoundState(this.wavLimit),
                    this.StateThreshold,
                    this.EntryWindowSec < 0
                        ? "отключено, событие роста размера не порождается"
                        : this.EntryWindowSec.ToString("F1", CultureInfo.InvariantCulture) + " сек",
                    this.DeadlineMinutes,
                    this.ExtensionProfit.ToString("F2", CultureInfo.InvariantCulture),
                    this.sessionAnchorUtc == DateTime.MinValue
                        ? "будет задан первым филом дня"
                        : DeadlineLocal(),
                    minutesTo930);

                WriteEvent("session_start", "TECH", null, ModuleVersion + " | " + selfCheck);
                Log("Behavior Alerter " + ModuleVersion + ": " + selfCheck, StrategyLoggingLevel.Info);

                if (minutesTo930 < 0)
                    WriteEvent("late_start", "TECH", null, "старт после 9:30 NY, " + Math.Abs(minutesTo930) + " мин");

                Core.Instance.TradeAdded += OnTradeAdded;

                // если модуль стартовал внутри открытой позиции, засеваем нетто из снимка,
                // иначе первый же фил классифицируется как вход
                var p0 = FindPosition();
                if (p0 != null)
                {
                    this.netQty = p0.Side == Side.Buy ? p0.Quantity : -p0.Quantity;
                    WriteEvent("net_seeded", "TECH", null,
                        "старт внутри позиции, нетто " + this.netQty.ToString("0.##", CultureInfo.InvariantCulture));
                }

                this.heartbeat = new Timer(OnHeartbeat, null, 500, 1000);
            }
            catch (Exception ex)
            {
                Log("Behavior Alerter не стартовал: " + ex.Message, StrategyLoggingLevel.Error);
            }
        }

        protected override void OnStop()
        {
            try
            {
                Core.Instance.TradeAdded -= OnTradeAdded;

                if (this.heartbeat != null)
                {
                    this.heartbeat.Dispose();
                    this.heartbeat = null;
                }

                WriteEvent("module_stopped", "TECH", null, "модуль остановлен");
            }
            catch { }
            finally
            {
                try
                {
                    // даём фоновому потоку дописать очередь, потом закрываем файл
                    this.running = false;
                    this.jobSignal.Set();
                    if (this.worker != null && !this.worker.Join(3000))
                        Log("worker не завершился за 3 сек", StrategyLoggingLevel.Error);
                    this.worker = null;
                }
                catch { }

                lock (this.ioLock)
                {
                    if (this.writer != null)
                    {
                        this.writer.Flush();
                        this.writer.Dispose();
                        this.writer = null;
                    }
                }
            }
        }

        protected override void OnRemove()
        {
            OnStop();
        }

#pragma warning disable CS0618, CS0672   // OnGetMetrics устарел, замена через Meter умеет только числа
        protected override List<StrategyMetric> OnGetMetrics()
        {
            var result = base.OnGetMetrics();
            var s = this.snap;                       // без блокировки: интерфейс не ждёт диск

            result.Add(Metric("СОСТОЯНИЕ", s.PauseLeft > 0 ? "ПАУЗА  " + s.PauseLeft + " сек" : "—"));
            result.Add(Metric("Счётчик состояния", s.State.ToString(CultureInfo.InvariantCulture)));
            result.Add(Metric("Без стопа, сек", s.NoStopSec < 0 ? "—" : s.NoStopSec.ToString(CultureInfo.InvariantCulture)));
            result.Add(Metric("Стопов подряд", s.Streak.ToString(CultureInfo.InvariantCulture)));
            result.Add(Metric("Выигрышей подряд", s.Wins.ToString(CultureInfo.InvariantCulture)));
            result.Add(Metric("Позиций за сессию", s.Trades.ToString(CultureInfo.InvariantCulture)));
            result.Add(Metric("Сессия, мин", s.SessionMin.ToString(CultureInfo.InvariantCulture)));
            result.Add(Metric("Последнее событие", s.Last));

            return result;
        }
#pragma warning restore CS0618, CS0672

        // вызывается только из хартбита, под sync
        private void RefreshSnap(DateTime nowUtc)
        {
            var gap = -1;
            if (this.active != null && this.active.StopMissingSinceUtc != DateTime.MinValue)
                gap = (int)(nowUtc - this.active.StopMissingSinceUtc).TotalSeconds;

            var left = (int)Math.Max(0, (this.pauseUntilUtc - nowUtc).TotalSeconds);
            var min = SessionElapsedSec(nowUtc) / 60;

            // новый объект только при реальном изменении: панель перерисовывается часто,
            // а между тактами состояние обычно то же самое
            var cur = this.snap;
            if (cur.PauseLeft == left && cur.NoStopSec == gap &&
                cur.Streak == this.consecutiveLosses && cur.Wins == this.consecutiveWins &&
                cur.Trades == this.tradesCount &&
                cur.SessionMin == min && cur.State == this.stateCounter &&
                cur.Last == this.lastEventText)
                return;

            this.snap = new Snap
            {
                PauseLeft = left,
                NoStopSec = gap,
                Streak = this.consecutiveLosses,
                Wins = this.consecutiveWins,
                Trades = this.tradesCount,
                SessionMin = min,
                State = this.stateCounter,
                Last = this.lastEventText
            };
        }

        // ------------------------------------------------------------------ что считать убытком

        private const int KindLoss = -1, KindScratch = 0, KindWin = 1;

        // ЕДИНСТВЕННОЕ место, где решается «убыток / скрэтч / прибыль».
        // A1, A2 и A5b обязаны спрашивать здесь: если каждый решит сам,
        // они разойдутся при первом изменении порога.
        private int ClassifyResult(double grossUsd, double size, out double ticksPerContract)
        {
            var n = Math.Max(1.0, size);
            var tv = this.TickValueUsd > 0 ? this.TickValueUsd : 0.5;
            ticksPerContract = Math.Round(grossUsd / (tv * n), 2);

            if (ticksPerContract < -this.ScratchBandTicks) return KindLoss;
            if (ticksPerContract > this.ScratchBandTicks) return KindWin;
            return KindScratch;
        }

        // ------------------------------------------------------------------ счётчик состояния

        // Четыре типа событий, и только они. Инкремент делается ДО записи события,
        // чтобы в самом событии уже стояло новое значение.
        // Вызывается под sync.
        private void BumpState(int which)
        {
            this.stateCounter++;
            switch (which)
            {
                case 1: this.cntFastReentry++; break;
                case 2: this.cntBypass++; break;
                case 3: this.cntNoStop++; break;
                case 4: this.cntSizeGrowth++; break;
            }
            this.stateDirty = true;
        }

        // ------------------------------------------------------------------ рубеж двух часов

        // Рубеж определён только когда есть якорь — первый фил дня.
        private DateTime DeadlineUtc()
        {
            return this.sessionAnchorUtc == DateTime.MinValue
                ? DateTime.MinValue
                : this.sessionAnchorUtc.AddMinutes(this.DeadlineMinutes);
        }

        private string DeadlineLocal()
        {
            var d = DeadlineUtc();
            return d == DateTime.MinValue
                ? ""
                : ToLocal(d).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        // Вызывается из такта, под sync. Модуль правило НЕ исполняет: только считает,
        // фиксирует решение и подаёт сигнал, если продление не заработано.
        private void CheckDeadline(DateTime nowUtc)
        {
            if (this.deadlineFired || this.sessionAnchorUtc == DateTime.MinValue)
                return;

            var deadline = DeadlineUtc();
            if (nowUtc < deadline)
                return;

            this.deadlineFired = true;
            this.stateDirty = true;

            // Прибыль ТОЛЬКО реализованная. Открытая позиция в расчёт не идёт,
            // поэтому берётся накопленный результат закрытых кругов, а не плавающий.
            var closed = Math.Round(this.sessionNet, 2);
            this.extensionAllowed = closed > this.ExtensionProfit;

            var detail = "рубеж " + this.DeadlineMinutes + " мин от первого фила достигнут" +
                         ", закрытая прибыль " + closed.ToString("F2", CultureInfo.InvariantCulture) +
                         " при пороге продления " + this.ExtensionProfit.ToString("F2", CultureInfo.InvariantCulture) +
                         ", продление " + (this.extensionAllowed ? "РАЗРЕШЕНО, срок не ограничен"
                                                                 : "НЕ РАЗРЕШЕНО, новых входов нет");

            if (this.extensionAllowed)
            {
                // Дальше модуль по этому правилу молчит — срок не ограничен.
                this.lastEventText = "рубеж пройден, продление разрешено";
                WriteEvent("session_deadline_reached", "INFO", this.active, detail,
                           closedProfit: closed, extensionAllowed: true);
            }
            else
            {
                Announce("ЛИМИТ", "Рубеж сессии пройден, продление не заработано. Новых входов нет.", this.wavLimit);
                WriteEvent("session_deadline_reached", "ЛИМИТ", this.active, detail,
                           closedProfit: closed, extensionAllowed: false);
            }
        }

        // Проверяется после записи события, чтобы порог не опережал причину.
        private void CheckStateThreshold(DateTime nowUtc)
        {
            if (this.thresholdFired || this.stateCounter < this.StateThreshold)
                return;

            this.thresholdFired = true;
            this.stateDirty = true;

            // Текст — УКАЗАНИЕ, а не отчёт. В момент подачи указание действует,
            // а число требует истолкования. Разбивка идёт в журнал, не в текст.
            const string order = "Порог пройден. Завершайте сессию: новых входов больше нет.";

            // Разбивка — в журнал.
            var detail = order +
                         " Счётчик " + this.stateCounter.ToString(CultureInfo.InvariantCulture) +
                         " при пороге " + this.StateThreshold.ToString(CultureInfo.InvariantCulture) +
                         ", быстрый перезаход " + this.cntFastReentry +
                         ", вход в паузу " + this.cntBypass +
                         ", без стопа " + this.cntNoStop +
                         ", рост размера " + this.cntSizeGrowth;

            var sym = this.Symbol != null ? this.Symbol.Name : "";

            this.lastEventText = "ЗАВЕРШИТЬ СЕССИЮ";

            Enqueue(() =>
            {
                try { PlayFileDirect(this.wavLimit); } catch { }

                // Вызов безусловный, в отличие от рядовых алертов. Он один за сессию.
                // НИКАКОЙ ТОРГОВОЙ ЛОГИКИ: заявки не отменяются, позиции не закрываются,
                // ввод ордеров не блокируется, параметры не меняются. Блокировка и
                // автозакрытие отклонены отдельным решением — строка стоит здесь,
                // чтобы они не появились из лучших побуждений.
                try { Core.Instance.Alert(order, sym, "", null, AlertNameSessionEnd); }
                catch (Exception ex) { Log("alert порога: " + ex.Message, StrategyLoggingLevel.Error); }

                try
                {
                    if (this.MirrorToPlatformLog)
                        Log("ЛИМИТ: " + detail, StrategyLoggingLevel.Trading);
                }
                catch { }
            });

            WriteEvent("state_threshold_reached", "ЛИМИТ", this.active, detail);
        }

        private static string KindName(int kind)
        {
            return kind == KindLoss ? "убыток" : kind == KindWin ? "прибыль" : "скрэтч";
        }

        private string BandNote()
        {
            return "полоса " + this.ScratchBandTicks.ToString(CultureInfo.InvariantCulture) + " тиков/контракт";
        }

        private static StrategyMetric Metric(string name, string value)
        {
            return new StrategyMetric { Name = name, FormattedValue = value };
        }

        // ------------------------------------------------------------------ фоновый поток

        private void Enqueue(Action a)
        {
            lock (this.jobs)
            {
                if (this.jobs.Count > 5000)      // страховка: очередь не растёт бесконечно
                    return;
                this.jobs.Enqueue(a);
            }
            this.jobSignal.Set();
        }

        private void WorkerLoop()
        {
            while (this.running)
            {
                Action job = null;
                lock (this.jobs)
                {
                    if (this.jobs.Count > 0)
                        job = this.jobs.Dequeue();
                }

                if (job == null)
                {
                    this.jobSignal.WaitOne(1000);   // сигнал будит сразу, таймаут — только страховка
                    continue;
                }

                try { job(); }
                catch (Exception ex) { Log("worker: " + ex.Message, StrategyLoggingLevel.Error); }
            }

            // дренаж остатка при остановке
            while (true)
            {
                Action job = null;
                lock (this.jobs)
                {
                    if (this.jobs.Count > 0)
                        job = this.jobs.Dequeue();
                }
                if (job == null) break;
                try { job(); } catch { }
            }
        }

        // ------------------------------------------------------------------ филы: вход, выход, добор
        //
        // Классификация филла по ЗНАКАМ нетто-позиции, а не по PositionImpactType.
        // Порядок обязателен: сначала направление, потом order_id.
        //   |prevNet| == 0                 -> ENTRY
        //   знак delta == знака prevNet     -> ADD     (и только здесь смотрим order_id)
        //   |newNet| == 0                   -> EXIT
        //   знак newNet != знака prevNet    -> REVERSE
        //   иначе                           -> REDUCE  (частичный выход)
        //
        // ADD проверяется раньше EXIT и по знаку, поэтому частичный выход в него
        // попасть не может. Закрытие определяется нулём нетто, а не уменьшением размера:
        // REDUCE оставляет позицию открытой, круг не закрывается, consecutive_losses
        // и таймеры A1/A2 не трогаются.
        // PositionImpactType остаётся только записью в логе, на логику не влияет.

        private const double Eps = 1e-9;

        private void OnTradeAdded(Trade t)
        {
            try
            {
                if (t == null)
                    return;
                if (this.Symbol != null && (t.Symbol == null || t.Symbol.Name != this.Symbol.Name))
                    return;

                var nowUtc = DateTime.UtcNow;
                var impact = t.PositionImpactType.ToString() ?? "";

                lock (this.sync)
                {
                    // Id наследуется от TradingObject.
                    // Один и тот же фил, пришедший дважды, уводит нетто мимо нуля
                    // и порождает ложную пару выход+вход.
                    var tid = t.Id;
                    if (!string.IsNullOrEmpty(tid) && !this.seenTradeIds.Add(tid))
                    {
                        WriteEvent("duplicate_fill", "TECH", this.active,
                            "повторный фил " + tid + ", проигнорирован");
                        return;
                    }
                    if (this.seenTradeIds.Count > 20000)
                    {
                        // Clear() открыл бы дорогу старому дублю. За сессию столько филов
                        // не бывает; если случилось — это само по себе повод для записи.
                        WriteEvent("module_error", "TECH", this.active,
                            "таблица филов переполнена, дедупликация сброшена");
                        this.seenTradeIds.Clear();
                    }
                }

                var gross = t.GrossPnl != null ? t.GrossPnl.Value : 0.0;
                var net = t.NetPnl != null ? t.NetPnl.Value : 0.0;
                var fee = t.Fee != null ? t.Fee.Value : 0.0;

                // Side наследуется от TradingObject, как у Order.
                var delta = t.Side == Side.Buy ? t.Quantity : -t.Quantity;

                lock (this.sync)
                {
                    if (this.sessionAnchorUtc == DateTime.MinValue)
                        this.sessionAnchorUtc = nowUtc;

                    var prevNet = this.netQty;
                    var newNet = prevNet + delta;
                    this.netQty = Math.Abs(newNet) < Eps ? 0.0 : newNet;

                    string kind;
                    if (Math.Abs(prevNet) < Eps)                         kind = "ENTRY";
                    else if (Math.Sign(delta) == Math.Sign(prevNet))     kind = "ADD";
                    else if (Math.Abs(newNet) < Eps)                     kind = "EXIT";
                    else if (Math.Sign(newNet) != Math.Sign(prevNet))    kind = "REVERSE";
                    else                                                 kind = "REDUCE";

                    switch (kind)
                    {
                        case "ENTRY":
                            // Вход, пришедший до подтверждения выхода, — подпись расхождения:
                            // реальный перезаход так быстро не случается.
                            if (this.exitPending && this.active != null)
                            {
                                Accumulate(gross, net, fee, t.OrderId);
                                var back = FindPosition();
                                var real = back != null
                                    ? (back.Side == Side.Buy ? back.Quantity : -back.Quantity)
                                    : newNet;
                                WriteEvent("module_error", "TECH", this.active,
                                    "вход через " +
                                    (nowUtc - this.exitPendingAtUtc).TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) +
                                    " сек после неподтверждённого выхода: круг не закрывался, " +
                                    "нетто выправлено на " + real.ToString("0.##", CultureInfo.InvariantCulture) +
                                    ", предыдущее событие " + this.lastEventType);
                                this.netQty = real;
                                this.active.Size = Math.Abs(real);
                                this.exitPending = false;
                                break;
                            }

                            OpenTrack(nowUtc, t, impact, "fill", Math.Abs(newNet));
                            Accumulate(gross, net, fee, t.OrderId);
                            break;

                        case "ADD":
                            Accumulate(gross, net, fee, t.OrderId);
                            ReportAdd(nowUtc, t, Math.Abs(prevNet), Math.Abs(newNet));
                            break;

                        case "REDUCE":
                            // частичный выход: результат копится, круг НЕ закрывается
                            Accumulate(gross, net, fee, t.OrderId);
                            if (this.active != null)
                            {
                                this.active.Size = Math.Abs(newNet);
                                this.active.PartialExits++;
                                WriteEvent("partial_exit", "INFO", this.active,
                                    "частичный выход, " + Math.Abs(prevNet).ToString("0.##", CultureInfo.InvariantCulture) +
                                    " -> " + Math.Abs(newNet).ToString("0.##", CultureInfo.InvariantCulture));
                            }
                            break;

                        case "EXIT":
                            // Выход = переход размера позиции к нулю. Источником
                            // размера служит платформа, а не внутренний счёт: если netQty
                            // разошлось, частичный выход даёт newNet == 0 на неверных числах.
                            // Счётчики ждут подтверждения из Core.Instance.Positions.
                            Accumulate(gross, net, fee, t.OrderId);
                            this.lastExitOrderId = t.OrderId ?? "";
                            if (this.active != null && !this.exitPending)
                            {
                                this.exitPending = true;
                                this.exitPendingAtUtc = nowUtc;
                                this.exitPendingSource = "fill";
                            }
                            break;

                        case "REVERSE":
                            // закрывающая часть принадлежит старому кругу, остаток открывает новый
                            Accumulate(gross, net, fee, t.OrderId);
                            this.lastExitOrderId = t.OrderId ?? "";
                            if (this.active != null)
                                FinalizeExit(this.active, nowUtc, "reverse");
                            OpenTrack(nowUtc, t, impact, "fill", Math.Abs(newNet));
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("OnTradeAdded: " + ex.Message, StrategyLoggingLevel.Error);
            }
        }

        // Добор объявляется только от заявки, отличной от входной,
        // и только один раз на заявку — сплит филлов одной заявки не добор.
        private void ReportAdd(DateTime nowUtc, Trade t, double before, double after)
        {
            if (this.active == null)
                return;

            this.active.Size = after;
            if (after > this.active.MaxSize)
                this.active.MaxSize = after;

            var oid = t.OrderId ?? "";
            var sec = (nowUtc - this.active.FirstFillUtc).TotalSeconds;

            // Граница между добором как частью входа и ростом уже сформированной
            // позиции — это ОКНО от первого фила входа, а не совпадение order_id.
            // Внутри окна размер продолжает формировать вход.
            var insideWindow = this.EntryWindowSec >= 0 && sec <= this.EntryWindowSec;
            var sameOrder = !string.IsNullOrEmpty(oid) && oid == this.active.TradeId;

            if (sameOrder || insideWindow)
            {
                if (!string.IsNullOrEmpty(oid))
                    this.active.EntryOrders.Add(oid);
                this.active.EntryOrderQty = after;
                this.active.LastEntryFillUtc = nowUtc;      // зазор считается от последнего фила входа
                if (this.active.EntryEvalPending)
                    this.active.EntryEvalAtUtc = nowUtc.AddSeconds(this.EntrySettleSec);
                return;
            }

            // Параметр не задан — событие не порождается вовсе. Лучше ноль, чем шум.
            if (this.EntryWindowSec < 0)
                return;

            if (!string.IsNullOrEmpty(oid) && !this.active.ReportedAddOrders.Add(oid))
                return;                                  // эта заявка уже объявлена

            var txt = "рост размера внутри позиции, " +
                      before.ToString("0.##", CultureInfo.InvariantCulture) + " -> " +
                      after.ToString("0.##", CultureInfo.InvariantCulture) +
                      ", через " + sec.ToString("F1", CultureInfo.InvariantCulture) +
                      " сек от первого фила, окно " +
                      this.EntryWindowSec.ToString("F1", CultureInfo.InvariantCulture) + " сек";

            BumpState(4);
            Signal("НАРУШЕНИЕ", txt);
            WriteEvent("size_increase", "НАРУШЕНИЕ", this.active, txt,
                       sizeBefore: before, sizeAfter: after, secSinceEntry: sec);
            CheckStateThreshold(nowUtc);
        }

        private void Accumulate(double gross, double net, double fee, string orderId)
        {
            if (this.active == null)
                return;
            this.active.RealizedGross += gross;
            this.active.RealizedNet += net;
            this.active.RealizedFee += fee;
            this.active.Fills++;
            if (string.IsNullOrEmpty(this.active.TradeId))
                this.active.TradeId = orderId ?? "";
        }

        private void OpenTrack(DateTime nowUtc, Trade t, string impact, string source, double size)
        {
            var tr = new Track
            {
                PositionKey = "",
                TradeId = t != null ? (t.OrderId ?? "") : "",
                SymbolName = t != null && t.Symbol != null ? t.Symbol.Name : (this.Symbol != null ? this.Symbol.Name : ""),
                EntrySource = source,
                EntryImpact = impact,
                OpenedUtc = nowUtc,
                EntrySize = size,
                Size = size,
                MaxSize = size,

                // Контекст фиксируется в момент открытия, а сама проверка —
                // после того как входная заявка дольётся.
                EntryOrderQty = size,
                FirstFillUtc = nowUtc,
                LastEntryFillUtc = nowUtc,
                EntryEventPending = true,
                EntryEvalPending = true,
                EntryEvalAtUtc = nowUtc.AddSeconds(this.EntrySettleSec),
                PrevCloseKind = this.lastCloseKind,
                PrevCloseTicks = this.lastCloseTicks,
                WinsAtOpen = this.consecutiveWins
            };

            if (!string.IsNullOrEmpty(tr.TradeId))
                tr.EntryOrders.Add(tr.TradeId);

            this.active = tr;
            this.tradesCount++;

            // Вход после рубежа без права продления — нарушение правила рубежа (rule3).
            // По журналу оно должно быть видно без ручного счёта дат и времён.
            if (this.deadlineFired && !this.extensionAllowed)
                tr.AfterDeadlineNoExtension = true;

            // Немедленный след пишется всегда, само событие entry — когда
            // станет известен зазор до стопа (падение модуля не должно стирать факт входа)
            WriteEvent("entry_pending", "TECH", tr, "вход зафиксирован, ожидание стопа");

            if (nowUtc < this.pauseUntilUtc)
            {
                var delta = (nowUtc - this.pauseStartedUtc).TotalSeconds;
                BumpState(2);
                WriteEvent("bypass_entry_during_pause", "ПАУЗА", tr, "вход во время отсчёта", true, delta);
                CheckStateThreshold(nowUtc);
            }

            // A1 ловит РЕШЕНИЕ перезайти после убытка. Два случая решением не являются
            // и звучать не должны, но обязаны оставить след в логе.
            if (this.lastLossExitUtc != DateTime.MinValue)
            {
                var since = (nowUtc - this.lastLossExitUtc).TotalSeconds;
                var sameOrder = !string.IsNullOrEmpty(tr.TradeId) && tr.TradeId == this.lastExitOrderId;

                if (since < this.FastReentrySec)
                {
                    if (sameOrder)
                    {
                        // разворот, залившийся несколькими филами: действие было одно
                        WriteEvent("reversal_leg", "TECH", tr,
                            "вход той же заявкой, что закрыла круг (" + tr.TradeId + "), A1 не подаётся");
                    }
                    else if (since < this.MinReentrySec)
                    {
                        // Интервал ниже порога означает дефект модуля.
                        // Подробности уложены в details: схема лога не меняется.
                        WriteEvent("module_error", "TECH", tr,
                            "A1 подавлен: интервал " + since.ToString("F2", CultureInfo.InvariantCulture) +
                            " сек ниже порога " + this.MinReentrySec.ToString("F1", CultureInfo.InvariantCulture) +
                            " сек, позиция " + (string.IsNullOrEmpty(tr.PositionKey) ? tr.TradeId : tr.PositionKey) +
                            ", предыдущее событие " + this.lastEventType);
                    }
                    else
                    {
                        var txt = "A1: вход через " + since.ToString("F1", CultureInfo.InvariantCulture) + " сек после убытка";
                        BumpState(1);
                        Signal("ПАУЗА", txt);
                        WriteEvent("A1_fast_reentry", "ПАУЗА", tr, txt, secSinceEntry: since);
                        CheckStateThreshold(nowUtc);
                    }
                }
            }

            this.stateDirty = true;
        }

        // ------------------------------------------------------------------ такт 1 сек

        private void OnHeartbeat(object state)
        {
            try
            {
                var nowUtc = DateTime.UtcNow;

                lock (this.sync)
                {
                    if (this.pauseUntilUtc != DateTime.MinValue && nowUtc >= this.pauseUntilUtc)
                    {
                        this.pauseUntilUtc = DateTime.MinValue;
                        WriteEvent("pause_finished", "INFO", null, "отсчёт завершён");
                    }

                    var p = FindPosition();

                    // запасной путь: филы не пришли, но позиция в снимке есть
                    if (p != null && this.active == null)
                    {
                        var signed = p.Side == Side.Buy ? p.Quantity : -p.Quantity;
                        this.netQty = signed;
                        OpenTrack(nowUtc, null, "", "snapshot", p.Quantity);
                        this.active.SymbolName = p.Symbol != null ? p.Symbol.Name : "";
                    }

                    // --- отложенный выход: подтверждаем платформой или объявляем дефект
                    if (this.active != null && this.exitPending)
                    {
                        if (p == null)
                        {
                            this.exitPending = false;
                            this.netQty = 0;
                            FinalizeExit(this.active, this.exitPendingAtUtc, this.exitPendingSource);
                        }
                        else if ((nowUtc - this.exitPendingAtUtc).TotalSeconds >= this.ExitConfirmSec)
                        {
                            // филы сказали «ноль», платформа держит позицию: это расхождение,
                            // а не выход. Счётчики не трогаем, чиним нетто, пишем дефект.
                            var real = p.Side == Side.Buy ? p.Quantity : -p.Quantity;
                            WriteEvent("module_error", "TECH", this.active,
                                "ложный выход отклонён: нетто по филам 0, платформа держит " +
                                real.ToString("0.##", CultureInfo.InvariantCulture) +
                                ", предыдущее событие " + this.lastEventType +
                                ", счётчики не изменены");
                            this.netQty = real;
                            this.active.Size = Math.Abs(real);
                            this.active.PartialExits++;
                            this.exitPending = false;
                            this.netMismatchTicks = 0;
                        }
                    }

                    if (this.active != null)
                    {
                        EvaluateEntrySize(this.active, nowUtc);

                        if (p != null)
                        {
                            // сверка накопленного нетто с платформой: расхождение означает
                            // пропущенный или задвоенный фил, и без коррекции оно копится весь день
                            var signed = p.Side == Side.Buy ? p.Quantity : -p.Quantity;
                            if (Math.Abs(signed - this.netQty) > 1e-6)
                            {
                                this.netMismatchTicks++;
                                if (this.netMismatchTicks >= 3)      // не реагируем на гонку между филом и снимком
                                {
                                    var was = this.netQty;
                                    this.netQty = signed;
                                    this.netMismatchTicks = 0;
                                    WriteEvent("net_resync", "TECH", this.active,
                                        "нетто разошлось с платформой: было " +
                                        was.ToString("0.##", CultureInfo.InvariantCulture) + ", стало " +
                                        signed.ToString("0.##", CultureInfo.InvariantCulture));
                                }
                            }
                            else
                            {
                                this.netMismatchTicks = 0;
                            }

                            this.active.ExitDetectedUtc = DateTime.MinValue;
                            this.active.PositionKey = p.Id;
                            UpdateSnapshot(this.active, p);
                            CheckStop(this.active, nowUtc);
                        }
                        else
                        {
                            // позиции нет: ждём закрывающий фил, потом фиксируем сами
                            if (this.active.ExitDetectedUtc == DateTime.MinValue)
                                this.active.ExitDetectedUtc = nowUtc;
                            else if ((nowUtc - this.active.ExitDetectedUtc).TotalSeconds >= this.ExitSettleSec)
                            {
                                this.netQty = 0;
                                FinalizeExit(this.active, nowUtc, "snapshot");
                            }
                        }
                    }

                    CheckDeadline(nowUtc);
                    RefreshSnap(nowUtc);
                }

                // диск — за пределами блокировки, чтобы поток интерфейса не ждал
                if (this.stateDirty)
                {
                    this.stateDirty = false;
                    PersistState();
                }
            }
            catch (Exception ex)
            {
                Log("heartbeat: " + ex.Message, StrategyLoggingLevel.Error);
            }
        }

        // ------------------------------------------------------------------ A5b

        // Проверка отложена до конца сбора филлов входной заявки, иначе вход на 10
        // контрактов, разбитый на десять филлов, оценивался бы как вход на 1.
        private void EvaluateEntrySize(Track tr, DateTime nowUtc)
        {
            if (!tr.EntryEvalPending || nowUtc < tr.EntryEvalAtUtc)
                return;

            tr.EntryEvalPending = false;

            var entry = Math.Max(tr.EntryOrderQty, tr.EntrySize);
            if (entry <= this.BaseSize)
                return;

            var head = "размер входа " + entry.ToString("0.##", CultureInfo.InvariantCulture) +
                       " при базовом " + this.BaseSize.ToString(CultureInfo.InvariantCulture);

            // Берётся непосредственно предыдущее закрытие, а не consecutive_losses.
            // A5b остаётся событием и статистической строкой, но в счётчик состояния
            // не входит и уровнем нарушения не помечается.
            if (tr.PrevCloseKind == KindLoss)
            {
                var txt = "A5b: " + head + ", предыдущая позиция " +
                          tr.PrevCloseTicks.ToString("F2", CultureInfo.InvariantCulture) +
                          " тиков/контракт, " + BandNote();
                WriteEvent("A5b_size_after_loss", "INFO", tr, txt,
                           sizeBefore: this.BaseSize, sizeAfter: entry);
            }
        }

        private void UpdateSnapshot(Track tr, Position p)
        {
            tr.Size = p.Quantity;
            if (p.Quantity > tr.MaxSize)
                tr.MaxSize = p.Quantity;

            tr.PnlTicks = p.GrossPnLTicks;
            tr.SnapshotNetPnl = p.NetPnL != null ? p.NetPnL.Value : 0.0;

            var stop = FindStop(p);
            tr.HasStop = stop != null;
            tr.StopDistanceTicks = stop != null ? StopDistanceTicks(p, stop) : -1;
        }

        // ------------------------------------------------------------------ A3

        private void CheckStop(Track tr, DateTime nowUtc)
        {
            if (tr.HasStop)
            {
                // Зазор до появления стопа фиксируется один раз, на каждом входе,
                // независимо от того, сработал A3 или нет.
                if (!tr.StopEverSeen)
                {
                    tr.StopEverSeen = true;
                    tr.StopGapSec = Math.Round((nowUtc - tr.LastEntryFillUtc).TotalSeconds, 1);
                    if (tr.StopGapSec < 0) tr.StopGapSec = 0;
                    ReleaseEntryEvent(tr, nowUtc);
                }

                tr.StopWasPresent = true;
                tr.StopMissingSinceUtc = DateTime.MinValue;
                tr.StopScheduleIdx = 0;
                tr.StopSoundFired = false;
                return;
            }

            if (tr.StopMissingSinceUtc == DateTime.MinValue)
                tr.StopMissingSinceUtc = nowUtc;

            var gap = (nowUtc - tr.StopMissingSinceUtc).TotalSeconds;

            // ближайшая непройденная отметка расписания
            var mark = ScheduleAt(tr.StopScheduleIdx);
            if (gap < mark)
                return;                                  // окна короче StopQuietSec не пишутся вовсе

            // Повторы НЕ схлопываются — так в счётчик попадает длительность окна
            while (gap >= ScheduleAt(tr.StopScheduleIdx + 1))
                tr.StopScheduleIdx++;
            var announced = ScheduleAt(tr.StopScheduleIdx);
            tr.StopScheduleIdx++;

            // Severity одинаковая у всех событий вида и от длительности не зависит.
            // Громкость включается с StopLoudSec — это про звук, не про уровень.
            var kind = tr.StopWasPresent ? "removed" : "never_set";
            var loud = gap >= this.StopLoudSec;

            var what = (tr.StopWasPresent ? "стоп снят, позиция без стопа " : "стоп не ставился, позиция без стопа ")
                       + ((int)gap) + " сек, размер " + tr.Size.ToString("0.##", CultureInfo.InvariantCulture);

            BumpState(3);

            if (loud && !tr.StopSoundFired)
            {
                tr.StopSoundFired = true;
                Signal("НАРУШЕНИЕ", what);
            }
            else
            {
                this.lastEventText = "НАРУШЕНИЕ " + what;
            }

            WriteEvent("A3_no_stop", "НАРУШЕНИЕ", tr, what,
                       stopGapSec: gap, scheduleMark: announced, noStopKind: kind);
            CheckStateThreshold(nowUtc);
        }

        // Выпускает придержанное событие entry, когда зазор до стопа известен.
        // Если стоп так и не появился, событие выпускается при закрытии круга.
        private void ReleaseEntryEvent(Track tr, DateTime nowUtc)
        {
            if (!tr.EntryEventPending)
                return;
            tr.EntryEventPending = false;

            WriteEvent("entry", "INFO", tr,
                "вход, размер " + Math.Max(tr.EntryOrderQty, tr.EntrySize).ToString("0.##", CultureInfo.InvariantCulture) +
                ", заявок " + tr.EntryOrders.Count +
                (tr.AfterDeadlineNoExtension ? ", ПОСЛЕ РУБЕЖА без права продления" : "") +
                (tr.StopEverSeen
                    ? ", зазор до стопа " + tr.StopGapSec.ToString("F1", CultureInfo.InvariantCulture) + " сек"
                    : ", стоп не появился за " + tr.StopGapSec.ToString("F1", CultureInfo.InvariantCulture) + " сек до закрытия"),
                stopGapSec: tr.StopGapSec, stopAppeared: tr.StopEverSeen,
                entryOrders: tr.EntryOrders.Count,
                entrySpanSec: Math.Round((tr.LastEntryFillUtc - tr.FirstFillUtc).TotalSeconds, 1));
        }

        private int ScheduleAt(int idx)
        {
            // первая отметка равна StopQuietSec, дальше фиксированное расписание, хвост каждые 300
            if (idx <= 0)
                return this.StopQuietSec;
            if (idx < StopSchedule.Length)
                return Math.Max(StopSchedule[idx], this.StopQuietSec);
            return StopSchedule[StopSchedule.Length - 1] + StopScheduleTail * (idx - StopSchedule.Length + 1);
        }

        // ------------------------------------------------------------------ выход

        private void FinalizeExit(Track tr, DateTime nowUtc, string source)
        {
            if (this.active != tr)
                return;

            double gross, net;
            string pnlSource;

            if (tr.Fills > 0)
            {
                gross = tr.RealizedGross;
                net = tr.RealizedNet;
                pnlSource = "trades";
            }
            else
            {
                gross = tr.SnapshotNetPnl;
                net = tr.SnapshotNetPnl;
                pnlSource = "snapshot";
            }

            this.lastExitUtc = nowUtc;

            // Если стоп так и не появился, entry всё равно обязан быть записан,
            // и stop_gap_sec обязан быть >= 0. Пишем фактическое время
            // от последнего фила входа до закрытия круга, а факт отсутствия стопа —
            // отдельным полем, иначе эти случаи не отличить от быстрой постановки.
            if (!tr.StopEverSeen)
                tr.StopGapSec = Math.Max(0, Math.Round((nowUtc - tr.LastEntryFillUtc).TotalSeconds, 1));
            ReleaseEntryEvent(tr, nowUtc);

            // Накопленный результат, внутридневной пик и просадка от него.
            // Просадка считается от ПЛЮСОВОГО пика дня, а не от нуля: так её
            // считают правила многих проп-счетов (trailing drawdown).
            this.sessionNet += net;
            if (this.sessionNet > this.peakNet)
                this.peakNet = this.sessionNet;
            this.ddFromPeak = Math.Round(this.peakNet - this.sessionNet, 2);

            // Серия — величина поведенческая, поэтому считается по gross в обе
            // стороны: комиссия к состоянию трейдера отношения не имеет.
            // Классификация — через единую функцию ClassifyResult. Вокруг нуля —
            // полоса скрэтча шириной ScratchBandTicks на контракт: тычок в один тик
            // серию не продлевает, но и сделка на +0.30 её не стирает.
            double resTicks;
            var kind = ClassifyResult(gross, tr.MaxSize, out resTicks);

            this.lastCloseKind = kind;
            this.lastCloseTicks = resTicks;

            var pauseNow = false;
            string streakEffect;

            if (kind == KindLoss)
            {
                // Два инкремента серии в пределах секунды физически невозможны.
                if (this.lastStreakIncUtc != DateTime.MinValue &&
                    (nowUtc - this.lastStreakIncUtc).TotalSeconds < 1.0)
                {
                    WriteEvent("module_error", "TECH", tr,
                        "инкремент серии подавлен: " +
                        (nowUtc - this.lastStreakIncUtc).TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) +
                        " сек после предыдущего, счётчик оставлен на " + this.consecutiveLosses +
                        ", предыдущее событие " + this.lastEventType);
                    streakEffect = "suppressed";
                }
                else
                {
                    this.consecutiveLosses++;
                    this.consecutiveWins = 0;
                    this.lastStreakIncUtc = nowUtc;
                    this.lastLossExitUtc = nowUtc;
                    streakEffect = "increment";
                    if (this.consecutiveLosses >= 2)
                    {
                        this.pauseStartedUtc = nowUtc;
                        this.pauseUntilUtc = nowUtc.AddSeconds(this.PauseSec);
                        pauseNow = true;
                    }
                }
            }
            else if (kind == KindWin)
            {
                this.consecutiveLosses = 0;
                this.consecutiveWins++;
                streakEffect = "reset";
            }
            else
            {
                // Скрэтч не убыток, но и напряжение не снимает.
                // Убыток, скрэтч, убыток — это два подряд.
                streakEffect = "scratch";
            }
            // ровно ноль счётчик не двигает

            WriteEvent("exit", "INFO", tr,
                "выход (" + source + "), gross " + gross.ToString("F2", CultureInfo.InvariantCulture) +
                ", netto " + net.ToString("F2", CultureInfo.InvariantCulture) +
                ", филов " + tr.Fills + ", " + KindName(kind) + " " +
                resTicks.ToString("F2", CultureInfo.InvariantCulture) + " тиков/контракт, " + BandNote() +
                ", серия " + streakEffect + ", подряд " + this.consecutiveLosses +
                ", выигрышей подряд " + this.consecutiveWins,
                pnlGross: gross, pnlNet: net, fee: tr.RealizedFee, pnlSource: pnlSource,
                streakEffect: streakEffect, scratchBand: this.ScratchBandTicks);

            this.active = null;

            if (pauseNow)
            {
                var n = this.consecutiveLosses;
                var txt = "A2: убыток подряд №" + n + ". Отсчёт " + this.PauseSec + " сек";

                // номер серии слышен: столько же низких ударов, сколько номер, до шести
                Announce("ПАУЗА", txt, PauseFileFor(n));

                WriteEvent("A2_loss_streak", "ПАУЗА", null, txt);
            }

            this.stateDirty = true;
        }

        // ------------------------------------------------------------------ вспомогательное

        private Position FindPosition()
        {
            var all = Core.Instance.Positions;
            for (int i = 0; i < all.Length; i++)
            {
                var p = all[i];
                if (p == null) continue;
                if (this.Symbol != null && (p.Symbol == null || p.Symbol.Name != this.Symbol.Name)) continue;
                return p;
            }
            return null;
        }

        private static Order FindStop(Position p)
        {
            if (p.StopLoss != null)
                return p.StopLoss;

            var orders = Core.Instance.Orders;
            for (int i = 0; i < orders.Length; i++)
            {
                var o = orders[i];
                if (o.Symbol == null || p.Symbol == null || o.Symbol.Name != p.Symbol.Name)
                    continue;
                if (o.Side == p.Side)
                    continue;
                var typeId = o.OrderTypeId ?? "";
                if (typeId.IndexOf("stop", StringComparison.OrdinalIgnoreCase) >= 0)
                    return o;
            }
            return null;
        }

        private static double StopDistanceTicks(Position p, Order stop)
        {
            if (stop == null || p.Symbol == null)
                return -1;

            var trigger = stop.TriggerPrice > 0 ? stop.TriggerPrice : stop.Price;
            if (trigger <= 0)
                return -1;

            var tick = p.Symbol.TickSize;
            if (tick <= 0)
                return -1;

            return Math.Round(Math.Abs(trigger - p.OpenPrice) / tick, 1);
        }

        private int SessionElapsedSec(DateTime nowUtc)
        {
            if (this.sessionAnchorUtc == DateTime.MinValue)
                return 0;
            return (int)(nowUtc - this.sessionAnchorUtc).TotalSeconds;
        }

        // ------------------------------------------------------------------ сигнал

        private void Signal(string severity, string text)
        {
            Announce(severity, text, severity == "ПАУЗА" ? this.wavPause : this.wavViolation);
        }

        // Единственная точка, из которой модуль шумит. Всё содержимое — фоновое:
        // PlaySound открывает звуковое устройство и читает файл, Strategy.Log и Alert
        // уходят в платформу. На потоке событий этому делать нечего.
        private static void PlayFileDirect(string wav)
        {
            if (!string.IsNullOrEmpty(wav) && File.Exists(wav))
                PlaySound(wav, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
        }

        private void Announce(string severity, string text, string wav)
        {
            this.lastEventText = severity + " " + text;

            var sym = this.Symbol != null ? this.Symbol.Name : "";
            var alert = this.UsePlatformAlert;

            Enqueue(() =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(wav) && File.Exists(wav))
                        PlaySound(wav, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
                }
                catch { }

                try
                {
                    if (alert)
                        Core.Instance.Alert(severity + ": " + text, sym, "", null, AlertNameRoutine);
                }
                catch { }

                try
                {
                    if (this.MirrorToPlatformLog)
                        Log(severity + ": " + text, StrategyLoggingLevel.Trading);
                }
                catch { }
            });
        }

        [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

        private const uint SND_ASYNC = 0x0001;
        private const uint SND_NODEFAULT = 0x0002;
        private const uint SND_FILENAME = 0x00020000;

        // ------------------------------------------------------------------ папка и звуки

        private static string ResolveFolder(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return configured.Trim();

            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs))
                docs = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return Path.Combine(docs, "TradingBehaviorLog");
        }

        // Существующие файлы не перезаписываются: свой звук просто кладётся вместо этих.
        private void EnsureSounds()
        {
            try
            {
                if (!File.Exists(this.wavPause))
                {
                    var s = new List<short>();
                    AppendTone(s, 165, 1.6, 0.55);
                    WriteWav(this.wavPause, s);
                }

                if (!File.Exists(this.wavViolation))
                {
                    var s = new List<short>();
                    for (int i = 0; i < 3; i++)
                    {
                        AppendTone(s, 1180, 0.09, 0.60);
                        AppendSilence(s, 0.07);
                    }
                    WriteWav(this.wavViolation, s);
                }

                if (!File.Exists(this.wavLimit))
                {
                    // порог счётчика состояния: двухтоновая сирена, ни на что не похожа
                    var s = new List<short>();
                    for (int i = 0; i < 4; i++)
                    {
                        AppendTone(s, 620, 0.20, 0.60);
                        AppendTone(s, 930, 0.20, 0.60);
                    }
                    WriteWav(this.wavLimit, s);
                }

                // A2: номер серии слышен количеством низких ударов. 2..6, дальше шесть.
                for (int n = 2; n <= MaxStreakSound; n++)
                {
                    var path = PauseFileFor(n);
                    if (File.Exists(path))
                        continue;
                    var s = new List<short>();
                    for (int i = 0; i < n; i++)
                    {
                        AppendTone(s, 165, 0.32, 0.55);
                        AppendSilence(s, 0.16);
                    }
                    WriteWav(path, s);
                }

                // Прогрев: файл, который ещё ни разу не звучал, читается с диска холодным.
                // Именно поэтому A2 подтормаживал заметнее прочих — pause_N каждый раз новый.
                Enqueue(() =>
                {
                    try
                    {
                        var buf = new byte[8192];
                        foreach (var f in Directory.GetFiles(this.folder, "*.wav"))
                        {
                            using (var fs = File.OpenRead(f))
                                while (fs.Read(buf, 0, buf.Length) > 0) { }
                        }
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                Log("создание звуков: " + ex.Message, StrategyLoggingLevel.Error);
            }
        }

        private static string SoundState(string path)
        {
            return File.Exists(path) ? "ok" : "создаётся";
        }

        private const int MaxStreakSound = 6;

        private string PauseFileFor(int streak)
        {
            var n = Math.Max(2, Math.Min(MaxStreakSound, streak));
            return Path.Combine(this.folder, "pause_" + n + ".wav");
        }

        private const int SampleRate = 44100;

        private static void AppendTone(List<short> buffer, double hz, double seconds, double amplitude)
        {
            int total = (int)(SampleRate * seconds);
            int fade = Math.Min(total / 4, (int)(SampleRate * 0.02));

            for (int i = 0; i < total; i++)
            {
                double env = 1.0;
                if (i < fade) env = (double)i / fade;
                else if (i > total - fade) env = (double)(total - i) / fade;

                double v = Math.Sin(2.0 * Math.PI * hz * i / SampleRate) * amplitude * env;
                buffer.Add((short)(v * short.MaxValue));
            }
        }

        private static void AppendSilence(List<short> buffer, double seconds)
        {
            int total = (int)(SampleRate * seconds);
            for (int i = 0; i < total; i++)
                buffer.Add(0);
        }

        private static void WriteWav(string path, List<short> samples)
        {
            int dataBytes = samples.Count * 2;

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + dataBytes);
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16);
                bw.Write((short)1);
                bw.Write((short)1);
                bw.Write(SampleRate);
                bw.Write(SampleRate * 2);
                bw.Write((short)2);
                bw.Write((short)16);
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(dataBytes);

                for (int i = 0; i < samples.Count; i++)
                    bw.Write(samples[i]);
            }
        }

        // ------------------------------------------------------------------ лог

        private void WriteEvent(string eventType, string severity, Track tr, string details,
                                bool bypass = false, double bypassDelta = 0,
                                double? stopGapSec = null, int? scheduleMark = null,
                                double? pnlGross = null, double? pnlNet = null, double? fee = null,
                                string pnlSource = null,
                                string streakEffect = null, double? scratchBand = null,
                                double? sizeBefore = null, double? sizeAfter = null,
                                double? secSinceEntry = null, string noStopKind = null,
                                bool? stopAppeared = null,
                                int? entryOrders = null, double? entrySpanSec = null,
                                double? closedProfit = null, bool? extensionAllowed = null)
        {
            lock (this.sync)
            {
                if (this.writer == null)
                    return;

                var utc = DateTime.UtcNow;
                var sb = new StringBuilder(900);

                sb.Append('{');
                Field(sb, "ts_local", ToLocal(utc).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                Field(sb, "ts_ny", ToNy(utc).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                Field(sb, "ts_utc", utc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                Field(sb, "event_type", eventType);
                Field(sb, "severity", severity);

                Field(sb, "symbol", tr != null ? tr.SymbolName : "");
                Field(sb, "trade_id", tr != null ? tr.TradeId : "");
                Field(sb, "position_key", tr != null ? tr.PositionKey : "");
                Field(sb, "entry_source", tr != null ? tr.EntrySource : "");
                Field(sb, "impact", tr != null ? tr.EntryImpact : "");

                Num(sb, "open_size", tr != null ? tr.Size : 0);
                // Итоговый размер входа, а не размер частичного фила
                Num(sb, "entry_size", tr != null ? Math.Max(tr.EntryOrderQty, tr.EntrySize) : 0);
                Num(sb, "entry_first_fill_size", tr != null ? tr.EntrySize : 0);
                Num(sb, "max_size", tr != null ? tr.MaxSize : 0);
                Num(sb, "base_size", this.BaseSize);

                Num(sb, "trades_count", this.tradesCount);
                Num(sb, "consecutive_losses", this.consecutiveLosses);
                Num(sb, "session_elapsed_sec", SessionElapsedSec(utc));
                Num(sb, "since_prev_exit_sec", this.lastExitUtc == DateTime.MinValue ? -1 : Math.Round((utc - this.lastExitUtc).TotalSeconds, 1));

                Field(sb, "has_stop", tr != null ? (tr.HasStop ? "yes" : "no") : "-");
                Field(sb, "stop_was_present", tr != null ? (tr.StopWasPresent ? "yes" : "no") : "-");
                Num(sb, "stop_distance_ticks", tr != null ? tr.StopDistanceTicks : -1);
                Num(sb, "stop_gap_sec", stopGapSec.HasValue ? Math.Round(stopGapSec.Value, 1)
                                        : (tr != null ? tr.StopGapSec : -1));
                Num(sb, "stop_mark_sec", scheduleMark.HasValue ? scheduleMark.Value : -1);
                Num(sb, "pnl_ticks", tr != null ? Math.Round(tr.PnlTicks, 1) : 0);

                Num(sb, "pnl_gross", pnlGross.HasValue ? Math.Round(pnlGross.Value, 2) : 0);
                Num(sb, "pnl_net", pnlNet.HasValue ? Math.Round(pnlNet.Value, 2) : 0);
                Num(sb, "fee", fee.HasValue ? Math.Round(fee.Value, 2) : 0);
                Field(sb, "pnl_source", pnlSource ?? "");
                Field(sb, "streak_effect", streakEffect ?? "");

                // Значение счётчика видно в момент ЛЮБОГО события
                Num(sb, "state_counter", this.stateCounter);
                Num(sb, "sc_fast_reentry", this.cntFastReentry);
                Num(sb, "sc_bypass", this.cntBypass);
                Num(sb, "sc_no_stop", this.cntNoStop);
                Num(sb, "sc_size_growth", this.cntSizeGrowth);
                Num(sb, "state_threshold", this.StateThreshold);

                // Накопленное, пик и просадка от пика
                Num(sb, "session_net", Math.Round(this.sessionNet, 2));
                Num(sb, "peak_net", Math.Round(this.peakNet, 2));
                Num(sb, "dd_from_peak", Math.Round(this.ddFromPeak, 2));

                // Всё, что участвует в счёте, лежит в полях, а не в тексте сообщения
                Num(sb, "size_before", sizeBefore.HasValue ? sizeBefore.Value : -1);
                Num(sb, "size_after", sizeAfter.HasValue ? sizeAfter.Value : -1);
                Num(sb, "sec_since_entry", secSinceEntry.HasValue ? Math.Round(secSinceEntry.Value, 1) : -1);
                Field(sb, "no_stop_kind", noStopKind ?? "");
                Field(sb, "stop_appeared", stopAppeared.HasValue ? (stopAppeared.Value ? "yes" : "no") : "");
                Num(sb, "entry_orders", entryOrders.HasValue ? entryOrders.Value : -1);
                Num(sb, "entry_span_sec", entrySpanSec.HasValue ? entrySpanSec.Value : -1);

                // Рубеж, решение о продлении и отметка нарушения правила рубежа
                Field(sb, "session_deadline", DeadlineLocal());
                Num(sb, "deadline_minutes", this.DeadlineMinutes);
                Num(sb, "extension_threshold", this.ExtensionProfit);
                Field(sb, "deadline_passed", this.deadlineFired ? "yes" : "no");
                Field(sb, "extension_allowed", this.deadlineFired ? (this.extensionAllowed ? "yes" : "no") : "");
                Field(sb, "rule3_violation", tr != null && tr.AfterDeadlineNoExtension ? "yes" : "no");
                Num(sb, "closed_profit_at_deadline", closedProfit.HasValue ? closedProfit.Value : -99999);
                Field(sb, "deadline_decision", extensionAllowed.HasValue
                        ? (extensionAllowed.Value ? "extend" : "stop") : "");
                Num(sb, "entry_window_sec", this.EntryWindowSec);
                Num(sb, "scratch_band", scratchBand.HasValue ? Math.Round(scratchBand.Value, 2) : -1);
                Num(sb, "fills", tr != null ? tr.Fills : 0);
                Num(sb, "partial_exits", tr != null ? tr.PartialExits : 0);
                Num(sb, "net_qty", this.netQty);

                Field(sb, "details", details);
                Field(sb, "bypass_flag", bypass ? "yes" : "no");
                Num(sb, "bypass_delta_sec", Math.Round(bypassDelta, 1), true);
                sb.Append('}');

                this.lastEventType = eventType;
                var line = sb.ToString();

                // Строка собрана из согласованного состояния — дальше её судьба
                // фоновая. Сброс на диск делается сразу после записи строки:
                // окно потери — миллисекунды очереди,
                // а не буфер, который копится до закрытия файла.
                Enqueue(() =>
                {
                    try
                    {
                        lock (this.ioLock)
                        {
                            if (this.writer == null)
                                return;
                            this.writer.WriteLine(line);
                            this.writer.Flush();
                        }
                    }
                    catch { }
                });
            }
        }

        private static void Field(StringBuilder sb, string key, string value, bool last = false)
        {
            sb.Append('"').Append(key).Append("\":\"").Append(Escape(value)).Append('"');
            if (!last) sb.Append(',');
        }

        private static void Num(StringBuilder sb, string key, double value, bool last = false)
        {
            sb.Append('"').Append(key).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
            if (!last) sb.Append(',');
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }

        // ------------------------------------------------------------------ состояние на диске

        private void PersistState()
        {
            try
            {
                List<string> lines;
                lock (this.sync)
                {
                    lines = new List<string>
                    {
                        "consecutive_losses=" + this.consecutiveLosses.ToString(CultureInfo.InvariantCulture),
                        "consecutive_wins=" + this.consecutiveWins.ToString(CultureInfo.InvariantCulture),
                        "state_counter=" + this.stateCounter.ToString(CultureInfo.InvariantCulture),
                        "sc1=" + this.cntFastReentry.ToString(CultureInfo.InvariantCulture),
                        "sc2=" + this.cntBypass.ToString(CultureInfo.InvariantCulture),
                        "sc3=" + this.cntNoStop.ToString(CultureInfo.InvariantCulture),
                        "sc4=" + this.cntSizeGrowth.ToString(CultureInfo.InvariantCulture),
                        "threshold_fired=" + (this.thresholdFired ? "1" : "0"),
                        "deadline_fired=" + (this.deadlineFired ? "1" : "0"),
                        "extension_allowed=" + (this.extensionAllowed ? "1" : "0"),
                        "session_net=" + this.sessionNet.ToString("R", CultureInfo.InvariantCulture),
                        "peak_net=" + this.peakNet.ToString("R", CultureInfo.InvariantCulture),
                        "trades_count=" + this.tradesCount.ToString(CultureInfo.InvariantCulture),
                        "session_anchor_utc=" + this.sessionAnchorUtc.ToString("O", CultureInfo.InvariantCulture),
                        "last_loss_exit_utc=" + this.lastLossExitUtc.ToString("O", CultureInfo.InvariantCulture),
                        "last_exit_utc=" + this.lastExitUtc.ToString("O", CultureInfo.InvariantCulture)
                    };
                }
                File.WriteAllLines(this.statePath, lines);   // диск вне блокировки
            }
            catch (Exception ex)
            {
                Log("persist: " + ex.Message, StrategyLoggingLevel.Error);
            }
        }

        private void RestoreState()
        {
            try
            {
                if (!File.Exists(this.statePath))
                    return;

                foreach (var line in File.ReadAllLines(this.statePath))
                {
                    var i = line.IndexOf('=');
                    if (i <= 0) continue;
                    var k = line.Substring(0, i);
                    var v = line.Substring(i + 1);

                    switch (k)
                    {
                        case "consecutive_losses": this.consecutiveLosses = int.Parse(v, CultureInfo.InvariantCulture); break;
                        case "consecutive_wins": this.consecutiveWins = int.Parse(v, CultureInfo.InvariantCulture); break;
                        case "state_counter": this.stateCounter = int.Parse(v, CultureInfo.InvariantCulture); break;
                        case "sc1": this.cntFastReentry = int.Parse(v, CultureInfo.InvariantCulture); break;
                        case "sc2": this.cntBypass = int.Parse(v, CultureInfo.InvariantCulture); break;
                        case "sc3": this.cntNoStop = int.Parse(v, CultureInfo.InvariantCulture); break;
                        case "sc4": this.cntSizeGrowth = int.Parse(v, CultureInfo.InvariantCulture); break;
                        case "threshold_fired": this.thresholdFired = v == "1"; break;
                        case "deadline_fired": this.deadlineFired = v == "1"; break;
                        case "extension_allowed": this.extensionAllowed = v == "1"; break;
                        case "session_net": this.sessionNet = double.Parse(v, CultureInfo.InvariantCulture); break;
                        case "peak_net": this.peakNet = double.Parse(v, CultureInfo.InvariantCulture); break;
                        case "trades_count": this.tradesCount = int.Parse(v, CultureInfo.InvariantCulture); break;
                        case "session_anchor_utc": this.sessionAnchorUtc = DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); break;
                        case "last_loss_exit_utc": this.lastLossExitUtc = DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); break;
                        case "last_exit_utc": this.lastExitUtc = DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); break;
                    }
                }

                this.ddFromPeak = Math.Round(this.peakNet - this.sessionNet, 2);
                WriteEvent("state_restored", "TECH", null,
                    "подряд " + this.consecutiveLosses + ", позиций " + this.tradesCount +
                    ", счётчик состояния " + this.stateCounter +
                    " (перезапуск состояние не прерывает)");
            }
            catch (Exception ex)
            {
                Log("restore: " + ex.Message, StrategyLoggingLevel.Error);
            }
        }

        // ------------------------------------------------------------------ время

        private static TimeZoneInfo ResolveNyTimeZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
            catch { }
            try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { }
            return TimeZoneInfo.Utc;
        }

        // Локальное время — по часовому поясу Windows.
        private static DateTime ToLocal(DateTime utc)
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local);
        }

        private static DateTime ToNy(DateTime utc)
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), NyTz);
        }

        private static int MinutesToNyOpen(DateTime utc)
        {
            var ny = ToNy(utc);
            var open = new DateTime(ny.Year, ny.Month, ny.Day, 9, 30, 0);
            return (int)Math.Round((open - ny).TotalMinutes);
        }
    }
}
