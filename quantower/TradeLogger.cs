// TradeLogger v19 — пассивный журнал сделок для Quantower.
// Код: 12.09.2026. Публикация: 24.11.2026 (код без изменений, очищены комментарии).
//
// ЧТО ДЕЛАЕТ
//   Ловит все исполнения на счёте, собирает из них позиции от flat до flat
//   и пишет по строке на круг: MAE/MFE в тиках и долларах, время в позиции,
//   чем закрылась, стоял ли стоп и какой риск позиция реально несла.
//   Ордеров не выставляет и ничего не меняет — только читает и пишет CSV.
//
// ФАЙЛЫ (ротация по неделям ISO, параметр Rotation)
//   trades_YYYY-Www.csv — круги. Основной файл для анализа.
//   fills_YYYY-Www.csv  — исполнения с типом ENTRY / ADD / REDUCE / EXIT.
//   events_YYYY-Www.csv — сырые события ДО фильтрации. Диагностический,
//                         по умолчанию ВЫКЛЮЧЕН.
//   Внимание: в файлах есть колонка account с именем счёта. Не публикуйте
//   их как есть.
//
// ГРАНИЦЫ КРУГА
//   Круг закрывается только когда нетто-позиция вернулась в ноль.
//   Уменьшение размера — частичный выход внутри того же круга.
//
// УСЛОВИЕ РАБОТЫ
//   Торговать только при запущенном модуле: тогда журнал полон по построению,
//   и число входов с итогом по нему можно считать. При старте в журнал
//   платформы пишется, с какого момента ведётся наблюдение.
//
// ---------------------------------------------------------------------------
// КЛЮЧЕВЫЕ РЕШЕНИЯ
//
//   Без привязки к символу. Пишутся все исполнения счёта; фильтры по счёту
//   и корню символа необязательны. Сравнение «MNQ» с именем контракта «MNQZ6»
//   молча отсеивало бы все филлы.
//
//   Единая точка отсчёта — СРЕДНЯЯ цена входа. От неё считаются результат,
//   дистанция стопа, риск и MAE/MFE. Поэтому MAE >= |убыток| выполняется
//   по построению, а флаг mae_lt_loss в колонке sanity ловит только
//   настоящие аномалии. first_entry_price сохранён для разбора набора позиции.
//
//   Цены исполнений идут в экстремумы наравне с тиками. Поток NewLast
//   платформа прореживает, и без цен филлов MAE могла бы выйти меньше
//   зафиксированного убытка.
//
//   Выход размечается по id ордера (STOP / TAKE), а не по близости цены.
//   Стоп опознаётся по TriggerPrice, а не по геометрии: у позиции, набранной
//   в плюс, геометрия путала стоп с тейком. Риск считается по фактическим
//   количествам всех защитных заявок с весами; непокрытый объём пишется
//   отдельно (uncovered_size_at_max_risk, max_uncovered_size).
//
//   Доборы и частичные выходы считаются по order_id, а не по филлам: заявка
//   на 10 контрактов может прийти десятью событиями Trade. kind=ADD получает
//   только новая заявка на увеличение, продолжение входной заявки остаётся
//   ENTRY, order_fill_no — номер исполнения внутри своей заявки.
//
//   Комиссия и чистый результат считаются по филлам. Рядом цифры брокера
//   как есть (broker_gross_usd, broker_net_usd): расхождение с собственным
//   расчётом само по себе диагностично. Денежные поля Trade читаются через
//   рефлексию, найденная форма один раз пишется в журнал платформы.
//
//   История за день намеренно не восстанавливается. Из истории сделок
//   известны цены и объёмы, но не стоп и не ход цены внутри позиции:
//   строки выглядели бы полными, а считать по ним было бы нельзя.
//
//   incomplete — почему строке нельзя верить полностью: restart (модуль
//   запущен при открытой позиции), no_ticks (за круг не пришло ни одного
//   тика, MAE/MFE только по ценам исполнений). Пусто — числам можно верить.
//
//   Торговые потоки не трогают диск и не перебирают ордера. CSV пишется
//   через очередь одним фоновым потоком с открытыми файлами, брекет
//   сканируется таймером раз в секунду, обработчик тиков берёт только
//   короткий лок своей позиции, вывод в журнал платформы — тоже в фоне.
//   Шаг и стоимость тика вычисляются один раз при открытии позиции.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using TradingPlatform.BusinessLayer;

namespace Alerter
{
    public class TradeLogger : Strategy
    {
        #region Параметры

        [InputParameter("Фильтр по счёту (пусто = все)", 0)]
        public Account AccountFilter;

        [InputParameter("Фильтр по корню символа (пусто = все)", 10)]
        public string SymbolRootFilter = "";

        [InputParameter("Папка логов (пусто = Документы)", 20)]
        public string LogFolder = "";

        [InputParameter("Ротация: daily / weekly / monthly", 25)]
        public string Rotation = "weekly";

        [InputParameter("Стоимость тика, $ (карта)", 30)]
        public string TickValueMap = "MNQ=0.50,MES=1.25,MYM=0.50,M2K=0.50,NQ=5.00,ES=12.50";

        [InputParameter("Стоимость тика по умолчанию, $", 40)]
        public double DefaultTickValue = 0.5;

        [InputParameter("Пауза перед чтением брекета, сек", 50)]
        public int BracketWaitSeconds = 3;

        [InputParameter("Период перечитки брекета, сек", 60)]
        public int BracketRescanSeconds = 5;

        [InputParameter("Допуск разметки выхода, тиков", 70)]
        public int ExitMatchTicks = 2;

        [InputParameter("Писать сырые события (диагностика)", 80)]
        public bool WriteRawEvents = false;

        #endregion

        #region Заголовки файлов

        private const string TradesHeader =
            "entry_time_utc,entry_time_ny,exit_time_utc,exit_time_ny,duration_sec," +
            "symbol,symbol_root,account,side," +
            "entry_size,max_size,adds,partial_exits,entry_fills,exit_fills," +
            "first_entry_price,avg_entry,avg_exit," +
            "result_ticks_per_contract,result_usd_gross," +
            "commission_usd,result_usd_net,broker_gross_usd,broker_net_usd," +
            "mae_ticks,mae_time_utc,mfe_ticks,mfe_time_utc,mae_usd,size_at_mae," +
            "run_min,run_max," +
            "exit_reason,entry_order_id,exit_order_id," +
            "has_stop,stop_ticks,has_take,take_ticks," +
            "risk_usd_at_entry,risk_usd_max,size_at_max_risk,stop_ticks_at_max_risk," +
            "uncovered_size_at_max_risk,max_uncovered_size," +
            "min_since_flat,incomplete,sanity\n";

        private const string FillsHeader =
            "time_utc,time_ny,symbol,symbol_root,account,order_id,trade_id," +
            "kind,order_fill_no,side,qty,price,net_after\n";

        private const string EventsHeader =
            "time_utc,time_ny,raw_symbol,raw_account,side,qty,price,order_id,accepted,reject_reason\n";

        #endregion

        #region Состояние

        private const double Eps = 1e-9;
        private static readonly TimeZoneInfo NyZone = ResolveNyZone();
        private static readonly Regex RootRegex =
            new Regex(@"^([A-Za-z]+?)[FGHJKMNQUVXZ]\d{1,2}$", RegexOptions.Compiled);

        // Позиции читаются из тикового потока без блокировки; мутации —
        // под коротким локом самой позиции (t.Sync)
        private readonly ConcurrentDictionary<string, OpenTrade> openBySymbol =
            new ConcurrentDictionary<string, OpenTrade>();

        // Нетто-позиции трогает только поток сделок, под gate
        private readonly object gate = new object();
        private readonly Dictionary<string, double> netBySymbol = new Dictionary<string, double>();
        private readonly Dictionary<string, Symbol> subscribed = new Dictionary<string, Symbol>();
        private readonly Dictionary<string, double> tickValues = new Dictionary<string, double>();

        private DateTime lastFlatUtc = DateTime.MinValue;

        // Платформа может продублировать событие: пишем исполнение один раз
        private readonly HashSet<string> seenTradeIds = new HashSet<string>();

        // С какого момента ведётся лог. Торговля идёт только при запущенном
        // модуле, поэтому файл полон по построению и число входов с итогом
        // по нему считаются. Отметка нужна не как предупреждение, а как факт:
        // при разборе видно, когда началось наблюдение.
        //
        // Восстанавливать что-либо из истории сделок намеренно не пытаемся:
        // оттуда известны только цены и объёмы, но не известно, где стоял стоп
        // и как ходила цена внутри позиции. Такие строки выглядели бы полными,
        // имея пустыми ровно те колонки, ради которых файл и ведётся.
        private volatile string dayCoverage = "—";
        private string logFolderResolved = "";

        private CsvWriter writer;
        private BackgroundRunner notifier;
        private Timer bracketTimer;
        private int bracketBusy;

        private int eventsReceived;
        private int fillsWritten;
        private int tradesWritten;
        private int sanityFlags;
        private volatile bool running;

        #endregion

        public TradeLogger()
            : base()
        {
            this.Name = "Trade Logger";
            this.Description = "Позиции flat-to-flat с MAE/MFE и фактическим риском";
        }

        #region Жизненный цикл

        protected override void OnCreated()
        {
        }

        protected override void OnRun()
        {
            this.ParseTickValues();

            if (!this.ResolveFolder())
            {
                this.Stop();
                return;
            }

            this.writer = new CsvWriter(this.WriterLog);
            this.notifier = new BackgroundRunner("TradeLogger notify",
                m => this.Log("Ошибка фоновой задачи: " + m, StrategyLoggingLevel.Error));
            this.running = true;

            Core.Instance.TradeAdded += this.Core_TradeAdded;

            // Один таймер на всё сканирование брекетов: торговые потоки
            // больше не перебирают ордера и не плодят отложенные задачи
            this.bracketTimer = new Timer(this.BracketTick, null, 1000, 1000);

            this.Log("=== TradeLogger запущен ===", StrategyLoggingLevel.Trading);
            this.Log($"Фильтр счёта: {(this.AccountFilter == null ? "ВСЕ" : this.AccountFilter.Name + " / " + this.AccountFilter.Id)}",
                     StrategyLoggingLevel.Trading);
            this.Log($"Фильтр корня символа: {(string.IsNullOrWhiteSpace(this.SymbolRootFilter) ? "ВСЕ" : this.SymbolRootFilter)}",
                     StrategyLoggingLevel.Trading);
            this.Log($"Папка логов: {this.logFolderResolved}, ротация: {this.RotationMode()}",
                     StrategyLoggingLevel.Trading);

            try
            {
                var accounts = Core.Instance.Accounts;
                if (accounts != null)
                    foreach (var a in accounts)
                        this.Log($"Доступный счёт: {a.Name} / {a.Id}", StrategyLoggingLevel.Trading);
            }
            catch (Exception ex)
            {
                this.Log($"Не удалось перечислить счета: {ex.Message}", StrategyLoggingLevel.Error);
            }

            this.RecoverOpenPositions();
            this.ReportDayCoverage();
        }

        #endregion

        #region Настройка

        private void ParseTickValues()
        {
            this.tickValues.Clear();

            if (string.IsNullOrWhiteSpace(this.TickValueMap))
                return;

            foreach (var part in this.TickValueMap.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                if (kv.Length != 2)
                    continue;

                double v;
                if (double.TryParse(kv[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                    this.tickValues[kv[0].Trim().ToUpperInvariant()] = v;
            }
        }

        private bool ResolveFolder()
        {
            try
            {
                this.logFolderResolved = string.IsNullOrWhiteSpace(this.LogFolder)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                   "Quantower Alerter")
                    : this.LogFolder;

                Directory.CreateDirectory(this.logFolderResolved);
                return true;
            }
            catch (Exception ex)
            {
                this.Log($"Не удалось создать папку логов: {ex.Message}", StrategyLoggingLevel.Error);
                return false;
            }
        }

        private string RotationMode()
        {
            string m = (this.Rotation ?? "weekly").Trim().ToLowerInvariant();
            return (m == "daily" || m == "monthly") ? m : "weekly";
        }

        /// <summary>
        /// Только строковые операции, без обращений к диску: вызывается
        /// из торговых потоков. Создание файла и проверку схемы делает
        /// поток записи, когда впервые увидит новый путь.
        /// </summary>
        private string PathFor(string kind)
        {
            var ny = ToNy(Core.Instance.TimeUtils.DateTimeUtcNow);
            string mode = this.RotationMode();

            string suffix;
            if (mode == "daily")
                suffix = ny.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            else if (mode == "monthly")
                suffix = ny.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            else
                suffix = string.Concat(ISOWeek.GetYear(ny).ToString(CultureInfo.InvariantCulture),
                                       "-W", ISOWeek.GetWeekOfYear(ny).ToString("00", CultureInfo.InvariantCulture));

            return Path.Combine(this.logFolderResolved, kind + "_" + suffix + ".csv");
        }

        private void WriterLog(string message, bool isError)
            => this.Log(message, isError ? StrategyLoggingLevel.Error : StrategyLoggingLevel.Trading);

        /// <summary>
        /// Перезапуск посреди сессии: поднимаем открытые позиции из ядра.
        /// MAE до перезапуска потеряна, строка помечается incomplete=restart.
        /// </summary>
        private void RecoverOpenPositions()
        {
            try
            {
                var positions = Core.Instance.Positions;
                if (positions == null)
                    return;

                foreach (var p in positions)
                {
                    if (p == null || p.Symbol == null)
                        continue;

                    string reject;
                    if (!this.PassesFilters(p.Symbol.Name, p.Account, out reject))
                        continue;

                    string name = p.Symbol.Name;
                    double signed = p.Side == Side.Buy ? p.Quantity : -p.Quantity;
                    var now = Core.Instance.TimeUtils.DateTimeUtcNow;

                    var t = new OpenTrade
                    {
                        SymbolName = name,
                        SymbolRef = p.Symbol,
                        AccountName = p.Account != null ? p.Account.Name : "",
                        AccountId = p.Account != null ? p.Account.Id : null,
                        Side = p.Side,
                        EntryUtc = now,
                        CreatedUtc = now,
                        FirstEntryPrice = p.OpenPrice,
                        CurrentSize = p.Quantity,
                        PeakSize = p.Quantity,
                        TickSize = this.TickSizeOf(p.Symbol),
                        TickValue = this.TickValueOf(name),
                        Recovered = true
                    };

                    t.InitExtremes(p.OpenPrice, now);
                    // Восстановленная позиция — одна условная заявка: разбивки
                    // по ордерам в снимке нет, и придумывать её нельзя
                    t.AddEntry(p.OpenPrice, p.Quantity, "recovered");

                    this.openBySymbol[name] = t;

                    lock (this.gate)
                    {
                        this.netBySymbol[name] = signed;
                        this.SubscribeUnsafe(p.Symbol);
                    }

                    this.Log($"Восстановлена открытая позиция: {name} {p.Side} {p.Quantity} @ {p.OpenPrice}",
                             StrategyLoggingLevel.Trading);
                }
            }
            catch (Exception ex)
            {
                this.Log($"Не удалось восстановить позиции: {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        /// <summary>
        /// Отмечает время начала наблюдения. Не предупреждение: торговля идёт
        /// только при запущенном модуле, поэтому лог полон по построению.
        /// Строка нужна, чтобы при разборе было видно границу наблюдения.
        /// </summary>
        private void ReportDayCoverage()
        {
            try
            {
                var ny = ToNy(Core.Instance.TimeUtils.DateTimeUtcNow);
                this.dayCoverage = ny.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture) + " NY";

                this.Log($"Лог ведётся с {this.dayCoverage}. Всё, что происходит с этого момента, " +
                         "попадает в файлы.", StrategyLoggingLevel.Trading);
            }
            catch (Exception ex)
            {
                this.Log($"Не удалось определить время начала: {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        protected override void OnStop()
        {
            this.running = false;

            Core.Instance.TradeAdded -= this.Core_TradeAdded;

            if (this.bracketTimer != null)
            {
                this.bracketTimer.Dispose();
                this.bracketTimer = null;
            }

            lock (this.gate)
            {
                foreach (var kv in this.subscribed)
                    if (kv.Value != null)
                        kv.Value.NewLast -= this.OnSymbolLast;

                this.subscribed.Clear();
            }

            // Незакрытые позиции не теряем — помечаем как неполные
            foreach (var kv in this.openBySymbol)
            {
                var t = kv.Value;
                t.ExitUtc = Core.Instance.TimeUtils.DateTimeUtcNow;
                t.ExitReasonOverride = "OPEN_AT_STOP";
                this.WriteTradeRow(t);
            }
            this.openBySymbol.Clear();

            if (this.notifier != null)
            {
                this.notifier.Complete();
                this.notifier = null;
            }

            int dropped = 0;
            if (this.writer != null)
            {
                dropped = this.writer.Dropped;
                this.writer.Complete();
                this.writer = null;
            }

            this.Log($"Остановлено. Событий: {this.eventsReceived}, филлов: {this.fillsWritten}, " +
                     $"кругов: {this.tradesWritten}, флагов sanity: {this.sanityFlags}" +
                     (dropped > 0 ? $", ПОТЕРЯНО СТРОК: {dropped}" : ""),
                     dropped > 0 ? StrategyLoggingLevel.Error : StrategyLoggingLevel.Trading);
        }

        protected override void OnRemove()
        {
        }

        [Obsolete]
        protected override List<StrategyMetric> OnGetMetrics()
        {
            List<StrategyMetric> result = base.OnGetMetrics();

            // Исключение здесь гасит всю панель, оставляя только базовые строки
            try
            {
                string pos = "плоско";
                string excursion = "—";
                string bracket = "—";
                string risk = "—";
                string file = "—";

                if (!this.openBySymbol.IsEmpty)
                {
                    var sb = new StringBuilder();
                    var ex = new StringBuilder();
                    var br = new StringBuilder();
                    var rk = new StringBuilder();

                    foreach (var kv in this.openBySymbol)
                    {
                        var t = kv.Value;

                        double size, mae, maeUsd, stop, uncovered, riskMax, tick;
                        int adds;
                        bool isLong;

                        // Короткий снимок под локом позиции: UI не должен ждать
                        lock (t.Sync)
                        {
                            tick = t.TickSize;
                            size = t.CurrentSize;
                            adds = t.Adds;
                            mae = t.MaeTicks(tick);
                            maeUsd = t.MaeUsd;
                            stop = t.CurrentStop;
                            uncovered = t.UncoveredNow;
                            riskMax = t.RiskUsdMax;
                            isLong = t.Side == Side.Buy;
                        }

                        if (sb.Length > 0) { sb.Append(" | "); ex.Append(" | "); br.Append(" | "); rk.Append(" | "); }

                        sb.Append($"{kv.Key} {(isLong ? "L" : "S")} {size:0.##}");
                        if (adds > 0)
                            sb.Append($" (+{adds})");

                        ex.Append($"MAE {mae:0.#}т / ${maeUsd:0.##}");

                        if (double.IsNaN(stop))
                            br.Append("СТОПА НЕТ");
                        else if (uncovered > 0)
                            br.Append($"стоп {stop:0.##}, НЕ ПРИКРЫТО {uncovered:0.##}");
                        else
                            br.Append($"стоп {stop:0.##}");

                        rk.Append(double.IsNaN(riskMax) ? "—" : $"${riskMax:0.##}");
                    }

                    pos = sb.ToString();
                    excursion = ex.ToString();
                    bracket = br.ToString();
                    risk = rk.ToString();
                }

                // Имя считается на месте: файлы создаёт поток записи,
                // а PathFor — чистая работа со строкой
                if (!string.IsNullOrEmpty(this.logFolderResolved))
                    file = Path.GetFileName(this.PathFor("trades"));

                result.Add(new StrategyMetric { Name = "Лог ведётся с", FormattedValue = this.dayCoverage });
                result.Add(new StrategyMetric { Name = "Позиции", FormattedValue = pos });
                result.Add(new StrategyMetric { Name = "Экскурсия", FormattedValue = excursion });
                result.Add(new StrategyMetric { Name = "Брекет", FormattedValue = bracket });
                result.Add(new StrategyMetric { Name = "Макс. риск", FormattedValue = risk });
                result.Add(new StrategyMetric { Name = "Событий получено", FormattedValue = this.eventsReceived.ToString(CultureInfo.InvariantCulture) });
                result.Add(new StrategyMetric { Name = "Филлов записано", FormattedValue = this.fillsWritten.ToString(CultureInfo.InvariantCulture) });
                result.Add(new StrategyMetric { Name = "Кругов записано", FormattedValue = this.tradesWritten.ToString(CultureInfo.InvariantCulture) });
                result.Add(new StrategyMetric { Name = "Флагов sanity", FormattedValue = this.sanityFlags.ToString(CultureInfo.InvariantCulture) });

                int q = this.writer != null ? this.writer.Pending : 0;
                int dropped = this.writer != null ? this.writer.Dropped : 0;
                result.Add(new StrategyMetric
                {
                    Name = "Очередь записи",
                    FormattedValue = dropped > 0 ? $"{q} (потеряно {dropped})" : q.ToString(CultureInfo.InvariantCulture)
                });

                result.Add(new StrategyMetric { Name = "Файл", FormattedValue = file });
            }
            catch (Exception ex)
            {
                result.Add(new StrategyMetric { Name = "ОШИБКА МЕТРИК", FormattedValue = ex.Message });
            }

            return result;
        }

        #endregion

        #region Приём событий

        private void Core_TradeAdded(Trade trade)
        {
            if (!this.running || trade == null)
                return;

            Interlocked.Increment(ref this.eventsReceived);

            // То же исполнение могло прийти из истории при старте
            if (!string.IsNullOrEmpty(trade.Id))
            {
                lock (this.seenTradeIds)
                {
                    if (!this.seenTradeIds.Add(trade.Id))
                        return;
                }
            }

            string rawSymbol = trade.Symbol != null ? trade.Symbol.Name : "";
            string rawAccount = trade.Account != null ? trade.Account.Name : "";

            string reject;
            bool accepted = this.PassesFilters(rawSymbol, trade.Account, out reject);

            if (this.WriteRawEvents)
                this.WriteEventRow(trade, rawSymbol, rawAccount, accepted, reject);

            if (!accepted)
                return;

            try
            {
                this.ProcessTrade(trade, rawSymbol);
            }
            catch (Exception ex)
            {
                // Ни одно исключение не должно исчезать молча
                this.Log($"ОШИБКА обработки сделки {rawSymbol}: {ex}", StrategyLoggingLevel.Error);
            }
        }

        private bool PassesFilters(string symbolName, Account account, out string reason)
        {
            reason = "";

            if (this.AccountFilter != null && account != null && account.Id != this.AccountFilter.Id)
            {
                reason = "account";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(this.SymbolRootFilter))
            {
                string root = RootOf(symbolName);
                if (!string.Equals(root, this.SymbolRootFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    reason = "symbol_root";
                    return false;
                }
            }

            return true;
        }

        private void ProcessTrade(Trade trade, string symbolName)
        {
            double delta = trade.Side == Side.Buy ? trade.Quantity : -trade.Quantity;

            OpenTrade finished = null;
            string kind;
            int fillNo = 0;
            double netAfter;
            Symbol symRef = trade.Symbol;

            double tick = this.TickSizeOf(symRef);
            double tv = this.TickValueOf(symbolName);
            string orderId = trade.OrderId;
            var nowUtc = Core.Instance.TimeUtils.DateTimeUtcNow;

            // Комиссия и реализованный результат от брокера — по каждому филлу
            double fee = this.ReadMoney(trade, "Fee");
            double bGross = this.ReadMoney(trade, "GrossPnl");
            double bNet = this.ReadMoney(trade, "NetPnl");

            lock (this.gate)
            {
                double prevNet;
                this.netBySymbol.TryGetValue(symbolName, out prevNet);

                double newNet = prevNet + delta;
                this.netBySymbol[symbolName] = newNet;
                netAfter = newNet;

                OpenTrade cur;
                this.openBySymbol.TryGetValue(symbolName, out cur);

                // Круг закрывается ТОЛЬКО при возврате нетто-позиции в ноль.
                // Порядок проверок важен: направление определяется раньше, чем
                // группировка по заявкам, иначе частичный выход попадёт в доборы.
                if (Math.Abs(prevNet) < Eps)
                    kind = "ENTRY";
                else if (Math.Sign(delta) == Math.Sign(prevNet))
                    kind = "ADD";
                else if (Math.Abs(newNet) < Eps)
                    kind = "EXIT";
                else if (Math.Sign(newNet) != Math.Sign(prevNet))
                    kind = "REVERSE";
                else
                    kind = "REDUCE";

                switch (kind)
                {
                    case "ENTRY":
                        cur = this.NewTrade(trade, symbolName, trade.Quantity, nowUtc);
                        this.openBySymbol[symbolName] = cur;
                        this.SubscribeUnsafe(symRef);
                        lock (cur.Sync)
                        {
                            cur.AddMoney(fee, bGross, bNet);
                            fillNo = cur.NextFillNo(orderId);
                        }
                        break;

                    case "ADD":
                        if (cur != null)
                            lock (cur.Sync)
                            {
                                // Позиция растёт — но это ещё не добор. Заявка
                                // на 10 контрактов приходит десятью событиями,
                                // и её продолжение остаётся тем же входом.
                                if (cur.IsFirstEntryOrder(orderId))
                                    kind = "ENTRY";

                                // Цена филла — реальная торгованная цена, кладём в экстремумы
                                cur.Touch(trade.Price, trade.DateTime, cur.CurrentSize, tick, tv, false);
                                cur.AddEntry(trade.Price, trade.Quantity, orderId);
                                cur.CurrentSize = Math.Abs(newNet);
                                cur.PeakSize = Math.Max(cur.PeakSize, cur.CurrentSize);
                                cur.RescanAsap = true;
                                cur.AddMoney(fee, bGross, bNet);
                                fillNo = cur.NextFillNo(orderId);
                            }
                        break;

                    case "REDUCE":
                        if (cur != null)
                            lock (cur.Sync)
                            {
                                // Размер берём ДО сокращения: в момент этой цены позиция была такой
                                cur.Touch(trade.Price, trade.DateTime, cur.CurrentSize, tick, tv, false);
                                cur.AddExit(trade.Price, trade.Quantity, orderId);
                                cur.CurrentSize = Math.Abs(newNet);
                                cur.AddMoney(fee, bGross, bNet);
                                fillNo = cur.NextFillNo(orderId);
                            }
                        break;

                    case "EXIT":
                        if (cur != null)
                        {
                            lock (cur.Sync)
                            {
                                cur.Touch(trade.Price, trade.DateTime, cur.CurrentSize, tick, tv, false);
                                cur.AddExit(trade.Price, trade.Quantity, orderId);
                                cur.CurrentSize = 0;
                                cur.ExitUtc = trade.DateTime;
                                cur.ExitOrderId = orderId;
                                cur.AddMoney(fee, bGross, bNet);
                                fillNo = cur.NextFillNo(orderId);
                            }

                            OpenTrade removed;
                            this.openBySymbol.TryRemove(symbolName, out removed);
                            finished = cur;
                            this.UnsubscribeUnsafe(symbolName);
                        }
                        this.lastFlatUtc = trade.DateTime;
                        break;

                    case "REVERSE":
                        if (cur != null)
                        {
                            lock (cur.Sync)
                            {
                                cur.Touch(trade.Price, trade.DateTime, cur.CurrentSize, tick, tv, false);
                                cur.AddExit(trade.Price, Math.Abs(prevNet), orderId);
                                cur.CurrentSize = 0;
                                cur.ExitUtc = trade.DateTime;
                                cur.ExitOrderId = orderId;
                                cur.ExitReasonOverride = "REVERSE";
                                cur.AddMoney(fee, bGross, bNet);
                                fillNo = cur.NextFillNo(orderId);
                            }
                            finished = cur;
                        }
                        this.lastFlatUtc = trade.DateTime;

                        cur = this.NewTrade(trade, symbolName, Math.Abs(newNet), nowUtc);
                        this.openBySymbol[symbolName] = cur;
                        this.SubscribeUnsafe(symRef);
                        break;
                }
            }

            // Обе записи только ставят строку в очередь — диска здесь нет
            this.WriteFillRow(trade, symbolName, kind, fillNo, netAfter);

            if (finished != null)
                this.WriteTradeRow(finished);
        }

        private OpenTrade NewTrade(Trade trade, string symbolName, double qty, DateTime nowUtc)
        {
            var t = new OpenTrade
            {
                SymbolName = symbolName,
                SymbolRef = trade.Symbol,
                AccountName = trade.Account != null ? trade.Account.Name : "",
                AccountId = trade.Account != null ? trade.Account.Id : null,
                Side = trade.Side,
                EntryUtc = trade.DateTime,
                CreatedUtc = nowUtc,
                EntryOrderId = trade.OrderId,
                FirstEntryPrice = trade.Price,
                CurrentSize = qty,
                PeakSize = qty,
                TickSize = this.TickSizeOf(trade.Symbol),
                TickValue = this.TickValueOf(symbolName)
            };

            t.InitExtremes(trade.Price, trade.DateTime);
            t.AddEntry(trade.Price, qty, trade.OrderId);

            t.MinSinceFlat = this.lastFlatUtc == DateTime.MinValue
                ? double.NaN
                : (trade.DateTime - this.lastFlatUtc).TotalMinutes;

            return t;
        }

        #endregion

        #region Подписки и тики

        /// <summary>Вызывать только под gate.</summary>
        private void SubscribeUnsafe(Symbol sym)
        {
            if (sym == null || this.subscribed.ContainsKey(sym.Name))
                return;

            sym.NewLast += this.OnSymbolLast;
            this.subscribed[sym.Name] = sym;
        }

        /// <summary>Вызывать только под gate.</summary>
        private void UnsubscribeUnsafe(string symbolName)
        {
            Symbol sym;
            if (!this.subscribed.TryGetValue(symbolName, out sym))
                return;

            if (sym != null)
                sym.NewLast -= this.OnSymbolLast;

            this.subscribed.Remove(symbolName);
        }

        /// <summary>
        /// Горячий путь. Здесь только поиск в словаре без блокировки и короткий
        /// лок самой позиции. Ни файлов, ни перебора ордеров, ни глобального лока:
        /// всё это задерживало бы доставку тиков графикам и стакану.
        /// </summary>
        private void OnSymbolLast(Symbol symbol, Last last)
        {
            if (!this.running || symbol == null)
                return;

            OpenTrade t;
            if (!this.openBySymbol.TryGetValue(symbol.Name, out t) || t == null)
                return;

            lock (t.Sync)
                t.Touch(last.Price, Core.Instance.TimeUtils.DateTimeUtcNow, t.CurrentSize,
                        t.TickSize, t.TickValue, true);
        }

        #endregion

        #region Брекет и измерение риска

        /// <summary>
        /// Единственное место, где перебираются ордера платформы. Работает
        /// в потоке таймера раз в секунду, поэтому ни поток сделок, ни поток
        /// котировок этой работы не несут.
        /// </summary>
        private void BracketTick(object state)
        {
            if (!this.running)
                return;

            // Пропускаем такт, если предыдущий ещё идёт
            if (Interlocked.CompareExchange(ref this.bracketBusy, 1, 0) != 0)
                return;

            try
            {
                if (this.openBySymbol.IsEmpty)
                    return;

                var nowUtc = Core.Instance.TimeUtils.DateTimeUtcNow;

                foreach (var kv in this.openBySymbol)
                {
                    var t = kv.Value;
                    if (t == null)
                        continue;

                    bool recordEntry = false;
                    bool needScan = false;

                    lock (t.Sync)
                    {
                        // Брекет появляется не мгновенно — первый замер откладываем
                        if (!t.EntryBracketRecorded &&
                            (nowUtc - t.CreatedUtc).TotalSeconds >= Math.Max(0, this.BracketWaitSeconds))
                        {
                            recordEntry = true;
                            needScan = true;
                        }

                        if (t.RescanAsap)
                        {
                            needScan = true;
                            t.RescanAsap = false;
                        }

                        if ((nowUtc - t.LastScanUtc).TotalSeconds >= Math.Max(1, this.BracketRescanSeconds))
                            needScan = true;

                        if (needScan)
                            t.LastScanUtc = nowUtc;
                    }

                    if (needScan)
                        this.ScanBracket(kv.Key, t, recordEntry);
                }
            }
            catch (Exception ex)
            {
                this.Log($"Ошибка сканирования брекета: {ex.Message}", StrategyLoggingLevel.Error);
            }
            finally
            {
                Volatile.Write(ref this.bracketBusy, 0);
            }
        }

        /// <summary>
        /// Читает встречные заявки и считает фактический риск позиции.
        ///
        /// Стоп опознаётся по наличию TriggerPrice, а не по геометрии относительно
        /// цены входа: стоп, подтянутый выше первой цены входа на позиции,
        /// набранной в плюс, попадал в «тейки», и риск переставал считаться вовсе.
        ///
        /// Риск считается по количествам всех защитных заявок, а не по одной
        /// ближайшей: стоп на 1 контракт при позиции в 2 — это прикрытая половина
        /// и голая половина, и это должно быть видно.
        /// </summary>
        private void ScanBracket(string symbolName, OpenTrade t, bool recordAsEntryBracket)
        {
            var orders = Core.Instance.Orders;
            if (orders == null)
                return;

            double tick = this.TickSizeOf(t.SymbolRef);
            double tv = this.TickValueOf(symbolName);
            if (tick <= 0)
                return;

            bool isLong = t.Side == Side.Buy;
            Side exitSide = isLong ? Side.Sell : Side.Buy;

            // Риск меряется от средней цены входа — это цена, от которой считаются
            // деньги. MAE/MFE считаются от неё же (MaeTicks / MfeTicks).
            double reference;
            lock (t.Sync)
                reference = t.AvgEntry;

            double stopQty = 0, stopQtyTicks = 0;
            double nearestStop = double.NaN, nearestStopDist = double.MaxValue;
            double nearestTake = double.NaN, nearestTakeDist = double.MaxValue;
            List<string> stopIds = null, takeIds = null;
            var raw = new List<BracketOrder>(8);
            bool anyTrigger = false;

            try
            {
                foreach (var o in orders)
                {
                    if (o == null || o.Symbol == null || o.Symbol.Name != symbolName)
                        continue;

                    if (t.AccountId != null && o.Account != null && o.Account.Id != t.AccountId)
                        continue;

                    if (o.Side != exitSide)
                        continue;

                    double trigger = o.TriggerPrice;
                    double price = trigger > 0 ? trigger : o.Price;
                    if (price <= 0)
                        continue;

                    double qty = o.RemainingQuantity > 0 ? o.RemainingQuantity : o.TotalQuantity;
                    if (qty <= 0)
                        continue;

                    if (trigger > 0)
                        anyTrigger = true;

                    // Положительные тики = убыток относительно средней цены входа
                    double signedTicks = isLong
                        ? (reference - price) / tick
                        : (price - reference) / tick;

                    raw.Add(new BracketOrder
                    {
                        Id = o.Id,
                        Price = price,
                        Qty = qty,
                        SignedTicks = signedTicks,
                        HasTrigger = trigger > 0
                    });
                }
            }
            catch (Exception ex)
            {
                // Коллекция ордеров могла измениться прямо во время перебора
                this.Log($"Перебор ордеров прерван: {ex.Message}", StrategyLoggingLevel.Error);
                return;
            }

            foreach (var c in raw)
            {
                // Есть TriggerPrice — стоп. Нет ни у одной заявки (брокер не заполняет) —
                // падаем на геометрию: невыгодная сторона от средней цены входа.
                bool isStop = anyTrigger ? c.HasTrigger : c.SignedTicks > 0;
                double d = Math.Abs(c.Price - reference);

                if (isStop)
                {
                    // Стоп, уведённый в прибыль, даёт отрицательные тики —
                    // риск по нему нулевой, а не отрицательный
                    stopQty += c.Qty;
                    stopQtyTicks += c.Qty * Math.Max(0, c.SignedTicks);

                    if (d < nearestStopDist) { nearestStopDist = d; nearestStop = c.Price; }

                    if (!string.IsNullOrEmpty(c.Id))
                        (stopIds ?? (stopIds = new List<string>(4))).Add(c.Id);
                }
                else
                {
                    if (d < nearestTakeDist) { nearestTakeDist = d; nearestTake = c.Price; }

                    if (!string.IsNullOrEmpty(c.Id))
                        (takeIds ?? (takeIds = new List<string>(4))).Add(c.Id);
                }
            }

            lock (t.Sync)
            {
                t.CurrentStop = nearestStop;
                t.CurrentTake = nearestTake;

                // Id накапливаются: заявку могли переставить, но именно она
                // в итоге исполнила выход
                if (stopIds != null)
                    foreach (var id in stopIds) t.KnownStopIds.Add(id);
                if (takeIds != null)
                    foreach (var id in takeIds) t.KnownTakeIds.Add(id);

                if (recordAsEntryBracket && !t.EntryBracketRecorded && !double.IsNaN(nearestStop))
                {
                    t.EntryStop = nearestStop;
                    t.EntryTake = nearestTake;
                    t.EntryBracketRecorded = true;
                }

                double size = t.CurrentSize;
                double uncovered = Math.Max(0, size - stopQty);

                t.UncoveredNow = uncovered;
                if (uncovered > t.MaxUncoveredSize)
                    t.MaxUncoveredSize = uncovered;

                if (stopQty <= 0 || size <= 0)
                    return; // без защитных заявок риск не определён — поле останется пустым

                // Средневзвешенная дистанция по всем защитным заявкам.
                // Умножается на ПОЛНЫЙ размер позиции, а не только на прикрытый:
                // непокрытые контракты никуда не делись. Сколько именно было
                // не прикрыто — в отдельной колонке.
                double weightedTicks = stopQtyTicks / stopQty;
                double usd = size * weightedTicks * tv;

                if (double.IsNaN(t.RiskUsdMax) || usd > t.RiskUsdMax)
                {
                    t.RiskUsdMax = usd;
                    t.SizeAtMaxRisk = size;
                    t.StopTicksAtMaxRisk = weightedTicks;
                    t.UncoveredAtMaxRisk = uncovered;
                }
            }
        }

        private struct BracketOrder
        {
            public string Id;
            public double Price;
            public double Qty;
            public double SignedTicks;
            public bool HasTrigger;
        }

        #endregion

        #region Формирование строк

        private void WriteEventRow(Trade trade, string rawSymbol, string rawAccount, bool accepted, string reject)
        {
            try
            {
                var sb = new StringBuilder(160);
                sb.Append(Ts(trade.DateTime)).Append(',');
                sb.Append(Ts(ToNy(trade.DateTime))).Append(',');
                sb.Append(Csv(rawSymbol)).Append(',');
                sb.Append(Csv(rawAccount)).Append(',');
                sb.Append(trade.Side == Side.Buy ? "BUY" : "SELL").Append(',');
                sb.Append(Num(trade.Quantity)).Append(',');
                sb.Append(Num(trade.Price)).Append(',');
                sb.Append(Csv(trade.OrderId)).Append(',');
                sb.Append(accepted ? "yes" : "no").Append(',');
                sb.Append(Csv(reject));
                sb.Append('\n');

                this.writer.Enqueue(this.PathFor("events"), EventsHeader, sb.ToString());
            }
            catch (Exception ex)
            {
                this.Log($"Ошибка формирования события: {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        private void WriteFillRow(Trade trade, string symbolName, string kind, int fillNo, double netAfter)
        {
            try
            {
                var sb = new StringBuilder(200);
                sb.Append(Ts(trade.DateTime)).Append(',');
                sb.Append(Ts(ToNy(trade.DateTime))).Append(',');
                sb.Append(Csv(symbolName)).Append(',');
                sb.Append(Csv(RootOf(symbolName))).Append(',');
                sb.Append(Csv(trade.Account != null ? trade.Account.Name : "")).Append(',');
                sb.Append(Csv(trade.OrderId)).Append(',');
                sb.Append(Csv(trade.Id)).Append(',');
                sb.Append(kind).Append(',');
                sb.Append(fillNo).Append(',');
                sb.Append(trade.Side == Side.Buy ? "BUY" : "SELL").Append(',');
                sb.Append(Num(trade.Quantity)).Append(',');
                sb.Append(Num(trade.Price)).Append(',');
                sb.Append(Num(netAfter));
                sb.Append('\n');

                this.writer.Enqueue(this.PathFor("fills"), FillsHeader, sb.ToString());
                Interlocked.Increment(ref this.fillsWritten);
            }
            catch (Exception ex)
            {
                this.Log($"Ошибка формирования фила: {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        private void WriteTradeRow(OpenTrade t)
        {
            try
            {
                double tick = this.TickSizeOf(t.SymbolRef);
                double tv = this.TickValueOf(t.SymbolName);
                bool isLong = t.Side == Side.Buy;

                string line;
                string sanity;
                double resultTicksPerContract, resultUsd, maeTicks, mfeTicks, riskUsdAtEntry;
                string reason;

                lock (t.Sync)
                {
                    double avgEntry = t.AvgEntry;
                    double avgExit = t.ExitQty > 0 ? t.ExitNotional / t.ExitQty : t.LastPrice;

                    // На контракт: средняя цена выхода против средней цены входа.
                    // Деньги по всей позиции — в result_usd_gross.
                    resultTicksPerContract = (isLong ? avgExit - avgEntry : avgEntry - avgExit) / tick;
                    resultUsd = resultTicksPerContract * tv * (t.ExitQty > 0 ? t.ExitQty : t.PeakSize);

                    maeTicks = t.MaeTicks(tick);
                    mfeTicks = t.MfeTicks(tick);

                    // От СРЕДНЕЙ цены входа, а не от первого фила: вход один,
                    // размер итоговый. От первой цены значение расходилось
                    // с stop_ticks_at_max_risk на тик при неизменном стопе.
                    double stopTicks = double.IsNaN(t.EntryStop) ? double.NaN
                        : Math.Abs(avgEntry - t.EntryStop) / tick;
                    double takeTicks = double.IsNaN(t.EntryTake) ? double.NaN
                        : Math.Abs(t.EntryTake - avgEntry) / tick;

                    // Риск, принятый при входе: полный объём ПЕРВОЙ заявки и исходный
                    // стоп. Не объём первого фила — заявка на 10 контрактов приходит
                    // десятью событиями, и по первому риск был бы занижен вдесятеро.
                    double entrySize = t.EntrySize;
                    riskUsdAtEntry = double.IsNaN(stopTicks) ? double.NaN : stopTicks * tv * entrySize;

                    reason = t.ExitReasonOverride ?? this.ClassifyExit(avgExit, t, tick);
                    sanity = BuildSanity(t, resultTicksPerContract, maeTicks, mfeTicks);

                    var sb = new StringBuilder(600);
                    sb.Append(Ts(t.EntryUtc)).Append(',');
                    sb.Append(Ts(ToNy(t.EntryUtc))).Append(',');
                    sb.Append(Ts(t.ExitUtc)).Append(',');
                    sb.Append(Ts(ToNy(t.ExitUtc))).Append(',');
                    sb.Append(Num((t.ExitUtc - t.EntryUtc).TotalSeconds)).Append(',');
                    sb.Append(Csv(t.SymbolName)).Append(',');
                    sb.Append(Csv(RootOf(t.SymbolName))).Append(',');
                    sb.Append(Csv(t.AccountName)).Append(',');
                    sb.Append(isLong ? "LONG" : "SHORT").Append(',');
                    sb.Append(Num(entrySize)).Append(',');
                    sb.Append(Num(t.PeakSize)).Append(',');
                    sb.Append(t.Adds).Append(',');
                    sb.Append(t.PartialExits).Append(',');
                    sb.Append(t.EntryFills).Append(',');
                    sb.Append(t.ExitFills).Append(',');
                    sb.Append(Num(t.FirstEntryPrice)).Append(',');
                    sb.Append(Num(avgEntry)).Append(',');
                    sb.Append(Num(avgExit)).Append(',');
                    sb.Append(Num(resultTicksPerContract)).Append(',');
                    sb.Append(Num(resultUsd)).Append(',');

                    // Комиссия и чистый результат: без брокерской выгрузки
                    // это единственный источник, а дневной итог считается
                    // именно по чистому
                    double commission = t.HasBrokerMoney ? t.FeeSum : double.NaN;
                    double netUsd = double.IsNaN(commission) ? double.NaN : resultUsd - commission;

                    sb.Append(Num(commission)).Append(',');
                    sb.Append(Num(netUsd)).Append(',');
                    sb.Append(t.HasBrokerMoney ? Num(t.BrokerGross) : "").Append(',');
                    sb.Append(t.HasBrokerMoney ? Num(t.BrokerNet) : "").Append(',');
                    sb.Append(Num(maeTicks)).Append(',');
                    sb.Append(Ts(t.MaeUtc)).Append(',');
                    sb.Append(Num(mfeTicks)).Append(',');
                    sb.Append(Ts(t.MfeUtc)).Append(',');
                    sb.Append(Num(t.MaeUsd)).Append(',');
                    sb.Append(Num(t.SizeAtMae)).Append(',');
                    sb.Append(Num(t.RunMin)).Append(',');
                    sb.Append(Num(t.RunMax)).Append(',');
                    sb.Append(reason).Append(',');
                    sb.Append(Csv(t.EntryOrderId)).Append(',');
                    sb.Append(Csv(t.ExitOrderId)).Append(',');
                    sb.Append(double.IsNaN(t.EntryStop) ? "no" : "yes").Append(',');
                    sb.Append(Num(stopTicks)).Append(',');
                    sb.Append(double.IsNaN(t.EntryTake) ? "no" : "yes").Append(',');
                    sb.Append(Num(takeTicks)).Append(',');
                    sb.Append(Num(riskUsdAtEntry)).Append(',');
                    sb.Append(Num(t.RiskUsdMax)).Append(',');
                    sb.Append(Num(t.SizeAtMaxRisk)).Append(',');
                    sb.Append(Num(t.StopTicksAtMaxRisk)).Append(',');
                    sb.Append(Num(t.UncoveredAtMaxRisk)).Append(',');
                    sb.Append(Num(t.MaxUncoveredSize)).Append(',');
                    sb.Append(Num(t.MinSinceFlat)).Append(',');
                    sb.Append(Csv(BuildIncomplete(t))).Append(',');
                    sb.Append(Csv(sanity));
                    sb.Append('\n');

                    line = sb.ToString();
                }

                if (sanity != "ok")
                    Interlocked.Increment(ref this.sanityFlags);

                this.writer.Enqueue(this.PathFor("trades"), TradesHeader, line);
                Interlocked.Increment(ref this.tradesWritten);

                string note = t.HasBrokerMoney
                    ? $", комиссия ${t.FeeSum:0.##}"
                    : ", комиссии нет";

                if (!double.IsNaN(t.RiskUsdMax) && !double.IsNaN(riskUsdAtEntry)
                    && t.RiskUsdMax > riskUsdAtEntry + 0.01)
                    note = $" | риск вырос ${riskUsdAtEntry:0.##} -> ${t.RiskUsdMax:0.##}";

                if (t.MaxUncoveredSize > 0)
                    note += $" | без стопа было {t.MaxUncoveredSize:0.##} контр.";

                if (t.Adds > 0)
                    note += $" | доборов {t.Adds}";

                var level = sanity == "ok" ? StrategyLoggingLevel.Trading : StrategyLoggingLevel.Error;
                string message = $"Круг: {t.SymbolName} {(isLong ? "LONG" : "SHORT")} " +
                                 $"{resultTicksPerContract:0.#}т/контракт, ${resultUsd:0.##}, " +
                                 $"MAE {maeTicks:0.#}т / ${t.MaeUsd:0.##}, {reason}" + note +
                                 (sanity == "ok" ? "" : $" | SANITY: {sanity}");

                // Лог уходит в интерфейс, а мы в потоке событий о сделках
                var runner = this.notifier;
                if (runner != null)
                    runner.Post(() => this.Log(message, level));
                else
                    this.Log(message, level);
            }
            catch (Exception ex)
            {
                this.Log($"Ошибка формирования круга: {ex}", StrategyLoggingLevel.Error);
            }
        }

        /// <summary>
        /// Почему числам в строке нельзя доверять полностью. Пусто — можно.
        ///
        /// Главный случай: если за жизнь позиции не пришло ни одного тика,
        /// MAE и MFE считаются по одним ценам исполнений и занижены, а строка
        /// при этом выглядит обычной.
        /// </summary>
        private static string BuildIncomplete(OpenTrade t)
        {
            List<string> flags = null;

            // Модуль запустился, когда позиция уже была открыта:
            // ни начала хода, ни брекета на входе мы не видели
            if (t.Recovered)
                (flags ?? (flags = new List<string>(2))).Add("restart");

            // Поток котировок молчал: экстремумы только по филлам
            if (t.TickTouches == 0)
                (flags ?? (flags = new List<string>(2))).Add("no_ticks");

            return flags == null ? "" : string.Join(";", flags);
        }

        /// <summary>
        /// Самопроверка. Убыточная сделка не может иметь MAE меньше убытка,
        /// выигрышная — MFE меньше прибыли, экстремум не может быть достигнут
        /// после выхода, а при известном стопе риск не может быть пустым.
        /// Не сошлось — флаг в строке, а не молчание.
        /// </summary>
        private static string BuildSanity(OpenTrade t, double resultTicks, double maeTicks, double mfeTicks)
        {
            List<string> flags = null;

            if (resultTicks < 0 && maeTicks + 0.001 < Math.Abs(resultTicks))
                (flags ?? (flags = new List<string>(2))).Add("mae_lt_loss");

            if (resultTicks > 0 && mfeTicks + 0.001 < resultTicks)
                (flags ?? (flags = new List<string>(2))).Add("mfe_lt_profit");

            if (t.ExitUtc != DateTime.MinValue && t.MaeUtc > t.ExitUtc.AddSeconds(1))
                (flags ?? (flags = new List<string>(2))).Add("mae_after_exit");

            if (t.ExitUtc != DateTime.MinValue && t.MfeUtc > t.ExitUtc.AddSeconds(1))
                (flags ?? (flags = new List<string>(2))).Add("mfe_after_exit");

            if (!double.IsNaN(t.EntryStop) && double.IsNaN(t.RiskUsdMax))
                (flags ?? (flags = new List<string>(2))).Add("risk_missing");

            // Доборы есть, а размер не вырос — значит группировка по order_id
            // где-то разошлась с реальностью
            if (t.Adds > 0 && t.PeakSize <= t.EntrySize + 1e-9)
                (flags ?? (flags = new List<string>(2))).Add("adds_without_growth");

            return flags == null ? "ok" : string.Join(";", flags);
        }

        /// <summary>
        /// Разметка выхода. Сначала точное сопоставление id закрывающего ордера
        /// с id стопа и тейка, которые видели при сканировании. Ценовая эвристика —
        /// только если id неизвестны, и тогда это видно по суффиксу _BY_PRICE.
        /// </summary>
        private string ClassifyExit(double exitPrice, OpenTrade t, double tick)
        {
            if (!string.IsNullOrEmpty(t.ExitOrderId))
            {
                if (t.KnownStopIds.Contains(t.ExitOrderId))
                    return "STOP";
                if (t.KnownTakeIds.Contains(t.ExitOrderId))
                    return "TAKE";
            }

            double tol = Math.Max(1, this.ExitMatchTicks) * tick;

            bool nearStop = !double.IsNaN(t.CurrentStop) && Math.Abs(exitPrice - t.CurrentStop) <= tol;
            bool nearTake = !double.IsNaN(t.CurrentTake) && Math.Abs(exitPrice - t.CurrentTake) <= tol;

            if (nearStop && nearTake)
                return "AMBIGUOUS";
            if (nearStop)
                return "STOP_BY_PRICE";
            if (nearTake)
                return "TAKE_BY_PRICE";

            return double.IsNaN(t.EntryStop) ? "HAND_NO_STOP" : "HAND";
        }

        #endregion

        #region Фоновая запись в CSV

        /// <summary>
        /// Один фоновый поток и открытые файлы вместо открыть-записать-закрыть
        /// на каждую строку. Торговые потоки только кладут готовую строку
        /// в очередь: раньше запись шла под общим локом прямо в потоке событий
        /// платформы, и на закрытии позиции таких записей было три подряд.
        /// </summary>
        private sealed class CsvWriter
        {
            private static readonly UTF8Encoding Utf8Bom = new UTF8Encoding(true);

            private struct Item
            {
                public string Path;
                public string Header;
                public string Text;
            }

            private readonly BlockingCollection<Item> queue;
            private readonly Dictionary<string, StreamWriter> writers = new Dictionary<string, StreamWriter>();
            private readonly Action<string, bool> log;
            private readonly Thread thread;
            private int dropped;

            public int Dropped => Volatile.Read(ref this.dropped);
            public int Pending => this.queue.Count;

            public CsvWriter(Action<string, bool> log, int capacity = 20000)
            {
                this.log = log;
                this.queue = new BlockingCollection<Item>(new ConcurrentQueue<Item>(), capacity);

                this.thread = new Thread(this.Loop)
                {
                    IsBackground = true,
                    Name = "TradeLogger CSV",
                    Priority = ThreadPriority.BelowNormal
                };
                this.thread.Start();
            }

            /// <summary>Не блокирует вызывающий поток. Переполнение считается, а не ждёт.</summary>
            public void Enqueue(string path, string header, string text)
            {
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(text))
                    return;

                try
                {
                    if (!this.queue.TryAdd(new Item { Path = path, Header = header, Text = text }))
                        Interlocked.Increment(ref this.dropped);
                }
                catch (InvalidOperationException)
                {
                    // Очередь уже закрыта на приём — стратегия останавливается
                }
            }

            public void Complete(int timeoutMs = 5000)
            {
                try { this.queue.CompleteAdding(); } catch { }
                try { this.thread.Join(timeoutMs); } catch { }
            }

            private void Loop()
            {
                var dirty = new HashSet<StreamWriter>();

                try
                {
                    foreach (var item in this.queue.GetConsumingEnumerable())
                    {
                        try
                        {
                            var w = this.GetWriter(item.Path, item.Header);
                            if (w != null)
                            {
                                w.Write(item.Text);
                                dirty.Add(w);
                            }
                        }
                        catch (Exception ex)
                        {
                            this.log("Ошибка записи в лог: " + ex.Message, true);
                        }

                        // Сбрасываем на диск, когда разгребли очередь: при пачке
                        // строк это один поход к диску вместо десяти
                        if (this.queue.Count == 0 && dirty.Count > 0)
                        {
                            foreach (var w in dirty)
                            {
                                try { w.Flush(); } catch { }
                            }
                            dirty.Clear();
                        }
                    }
                }
                catch (Exception ex)
                {
                    this.log("Поток записи логов остановлен: " + ex.Message, true);
                }
                finally
                {
                    foreach (var kv in this.writers)
                    {
                        try { kv.Value.Flush(); kv.Value.Dispose(); } catch { }
                    }
                    this.writers.Clear();
                }
            }

            private StreamWriter GetWriter(string path, string header)
            {
                StreamWriter w;
                if (this.writers.TryGetValue(path, out w))
                    return w;

                bool needHeader = true;

                if (File.Exists(path))
                {
                    string first = null;
                    try
                    {
                        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                       FileShare.ReadWrite | FileShare.Delete))
                        using (var sr = new StreamReader(fs, Utf8Bom))
                            first = sr.ReadLine();
                    }
                    catch { }

                    if (first != null && first.TrimStart('\uFEFF') == header.TrimEnd('\n'))
                    {
                        needHeader = false;
                    }
                    else
                    {
                        // Схема изменилась — старый файл уезжает в .bak,
                        // а не дописывается строками с другим числом колонок
                        try
                        {
                            string bak = path + "." + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + ".bak";
                            File.Move(path, bak);
                            this.log("Схема лога изменилась, старый файл переименован в " +
                                     Path.GetFileName(bak), false);
                        }
                        catch (Exception ex)
                        {
                            this.log("Не удалось отложить старый файл: " + ex.Message, true);
                            return null;
                        }
                    }
                }

                var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                                            FileShare.ReadWrite | FileShare.Delete, 8192);
                w = new StreamWriter(stream, Utf8Bom) { AutoFlush = false };

                if (needHeader)
                    w.Write(header);

                this.writers[path] = w;
                this.log("Файл лога: " + path, false);
                return w;
            }
        }

        /// <summary>
        /// Запись в лог Strategy Runner уходит в интерфейс и может быть небыстрой,
        /// а вызывается она в потоке событий о сделках — то есть ровно в момент
        /// закрытия позиции. Поэтому исполняется отдельным потоком.
        /// </summary>
        private sealed class BackgroundRunner
        {
            private readonly BlockingCollection<Action> queue =
                new BlockingCollection<Action>(new ConcurrentQueue<Action>(), 2000);
            private readonly Thread thread;
            private readonly Action<string> onError;
            private int dropped;

            public int Dropped => Volatile.Read(ref this.dropped);

            public BackgroundRunner(string name, Action<string> onError)
            {
                this.onError = onError;
                this.thread = new Thread(this.Loop)
                {
                    IsBackground = true,
                    Name = name,
                    Priority = ThreadPriority.Normal
                };
                this.thread.Start();
            }

            public void Post(Action action)
            {
                if (action == null)
                    return;

                try
                {
                    if (!this.queue.TryAdd(action))
                        Interlocked.Increment(ref this.dropped);
                }
                catch (InvalidOperationException)
                {
                    // Очередь закрыта — стратегия останавливается
                }
            }

            public void Complete(int timeoutMs = 5000)
            {
                try { this.queue.CompleteAdding(); } catch { }
                try { this.thread.Join(timeoutMs); } catch { }
            }

            private void Loop()
            {
                try
                {
                    foreach (var action in this.queue.GetConsumingEnumerable())
                    {
                        try { action(); }
                        catch (Exception ex) { this.onError(ex.Message); }
                    }
                }
                catch (Exception ex)
                {
                    this.onError("поток доставки остановлен: " + ex.Message);
                }
            }
        }

        #endregion

        #region Модель круга

        private class OpenTrade
        {
            // Короткий лок на саму позицию: тиковый поток не должен ждать
            // ни глобальный лок стратегии, ни тем более диск
            public readonly object Sync = new object();

            public string SymbolName;
            public Symbol SymbolRef;
            public string AccountName;
            public string AccountId;

            public DateTime EntryUtc;
            public DateTime ExitUtc;
            public DateTime CreatedUtc;
            public Side Side;

            public double FirstEntryPrice;

            // Считаются один раз при создании позиции. Раньше стоимость тика
            // вычислялась на каждом тике, а внутри неё шло регулярное выражение
            // по имени контракта — лишняя работа в самом горячем месте.
            public double TickSize = 0.25;
            public double TickValue = 0.5;
            public double EntryNotional;
            public double EntryQty;
            public double ExitNotional;
            public double ExitQty;

            public double CurrentSize;
            public double PeakSize;

            // Учёт по ЗАЯВКАМ, а не по филлам: одна заявка на 10 контрактов
            // приходит десятью событиями Trade и добором не является
            private readonly List<string> entryOrderIds = new List<string>(2);
            private readonly Dictionary<string, double> qtyByEntryOrder = new Dictionary<string, double>(2);
            private readonly HashSet<string> exitOrderIds = new HashSet<string>();
            private readonly Dictionary<string, int> fillsByOrder = new Dictionary<string, int>(4);

            public int EntryFills;
            public int ExitFills;

            public double RunMin;
            public double RunMax;
            public DateTime MinUtc;
            public DateTime MaxUtc;
            public double LastPrice;

            public double MaeUsd;
            public double SizeAtMae;

            // Сколько раз экстремумы обновлялись из потока котировок
            public int TickTouches;

            // Деньги от брокера, суммируются по всем исполнениям круга.
            // Свой расчёт валового результата остаётся рядом — расхождение
            // между ними само по себе диагностично.
            public double FeeSum;
            public double BrokerGross;
            public double BrokerNet;
            public bool HasBrokerMoney;

            public void AddMoney(double fee, double gross, double net)
            {
                if (!double.IsNaN(fee)) { this.FeeSum += fee; this.HasBrokerMoney = true; }
                if (!double.IsNaN(gross)) { this.BrokerGross += gross; this.HasBrokerMoney = true; }
                if (!double.IsNaN(net)) { this.BrokerNet += net; this.HasBrokerMoney = true; }
            }

            // Максимальный риск за жизнь позиции. Поля пишутся одной операцией,
            // поэтому в файле всегда выполняется
            // risk_usd_max = size_at_max_risk * stop_ticks_at_max_risk * tick_value
            public double RiskUsdMax = double.NaN;
            public double SizeAtMaxRisk = double.NaN;
            public double StopTicksAtMaxRisk = double.NaN;
            public double UncoveredAtMaxRisk = double.NaN;

            public double UncoveredNow;
            public double MaxUncoveredSize;

            public double EntryStop = double.NaN;
            public double EntryTake = double.NaN;
            public double CurrentStop = double.NaN;
            public double CurrentTake = double.NaN;
            public bool EntryBracketRecorded;

            // Управление фоновым сканированием
            public DateTime LastScanUtc = DateTime.MinValue;
            public bool RescanAsap;

            public readonly HashSet<string> KnownStopIds = new HashSet<string>();
            public readonly HashSet<string> KnownTakeIds = new HashSet<string>();
            public string EntryOrderId;
            public string ExitOrderId;

            public double MinSinceFlat = double.NaN;
            public string ExitReasonOverride;
            public bool Recovered;

            public double AvgEntry => this.EntryQty > 0 ? this.EntryNotional / this.EntryQty : this.FirstEntryPrice;

            // Какой из двух экстремумов невыгодный, зависит от стороны позиции:
            // для лонга худшая точка — минимум, для шорта — максимум
            public DateTime MaeUtc => this.Side == Side.Buy ? this.MinUtc : this.MaxUtc;
            public DateTime MfeUtc => this.Side == Side.Buy ? this.MaxUtc : this.MinUtc;

            /// <summary>Полный объём ПЕРВОЙ заявки, а не первого фила.</summary>
            public double EntrySize
            {
                get
                {
                    if (this.entryOrderIds.Count == 0)
                        return 0;

                    double q;
                    return this.qtyByEntryOrder.TryGetValue(this.entryOrderIds[0], out q) ? q : 0;
                }
            }

            /// <summary>Число заявок на увеличение позиции после первой.</summary>
            public int Adds => Math.Max(0, this.entryOrderIds.Count - 1);

            /// <summary>
            /// Заявка, которой позиция была открыта. Её последующие исполнения —
            /// продолжение входа, а не добор.
            /// </summary>
            public bool IsFirstEntryOrder(string orderId)
            {
                if (this.entryOrderIds.Count == 0)
                    return false;

                return this.entryOrderIds[0] == (string.IsNullOrEmpty(orderId) ? "" : orderId);
            }

            /// <summary>Порядковый номер исполнения внутри своей заявки: 1, 2, 3...</summary>
            public int NextFillNo(string orderId)
            {
                string key = string.IsNullOrEmpty(orderId) ? "" : orderId;

                int n;
                this.fillsByOrder.TryGetValue(key, out n);
                n++;
                this.fillsByOrder[key] = n;
                return n;
            }

            /// <summary>Число выходов до финального, по заявкам.</summary>
            public int PartialExits => Math.Max(0, this.exitOrderIds.Count - 1);

            public void InitExtremes(double price, DateTime utc)
            {
                this.RunMin = this.RunMax = price;
                this.MinUtc = this.MaxUtc = utc;
                this.LastPrice = price;
            }

            /// <summary>
            /// Единая точка обновления экстремумов — вызывается и из тиков,
            /// и из филлов. size — размер позиции в этот момент: просадка
            /// в тиках на одном контракте и на четырёх это разные деньги.
            /// </summary>
            /// <summary>
            /// fromTick=true — обновление пришло из потока котировок,
            /// false — это цена исполнения. Различать нужно, чтобы поймать
            /// случай, когда тиков за жизнь позиции не было вовсе: тогда
            /// MAE и MFE посчитаны по одним филлам и занижены, а строка
            /// выглядит нормальной.
            /// </summary>
            public void Touch(double price, DateTime utc, double size, double tick,
                              double tickValue, bool fromTick)
            {
                if (price <= 0)
                    return;

                if (fromTick)
                    this.TickTouches++;

                if (price < this.RunMin) { this.RunMin = price; this.MinUtc = utc; }
                if (price > this.RunMax) { this.RunMax = price; this.MaxUtc = utc; }

                this.LastPrice = price;

                if (tick <= 0 || size <= 0)
                    return;

                // Средняя на момент замера: после добора она сдвигается,
                // и просадка должна считаться от новой средней
                double entry = this.AvgEntry;
                double adverseTicks = this.Side == Side.Buy
                    ? (entry - price) / tick
                    : (price - entry) / tick;

                if (adverseTicks <= 0)
                    return;

                double usd = adverseTicks * tickValue * size;
                if (usd > this.MaeUsd)
                {
                    this.MaeUsd = usd;
                    this.SizeAtMae = size;
                }
            }

            public void AddEntry(double price, double qty, string orderId)
            {
                this.EntryFills++;
                this.EntryNotional += price * qty;
                this.EntryQty += qty;

                // Пустой id складываем в общий ковш: лучше недосчитать доборы,
                // чем насчитать их по числу филлов
                string key = string.IsNullOrEmpty(orderId) ? "" : orderId;

                double prev;
                if (this.qtyByEntryOrder.TryGetValue(key, out prev))
                {
                    this.qtyByEntryOrder[key] = prev + qty;
                }
                else
                {
                    this.qtyByEntryOrder[key] = qty;
                    this.entryOrderIds.Add(key);
                }
            }

            public void AddExit(double price, double qty, string orderId)
            {
                this.ExitFills++;
                this.ExitNotional += price * qty;
                this.ExitQty += qty;
                this.LastPrice = price;

                this.exitOrderIds.Add(string.IsNullOrEmpty(orderId) ? "" : orderId);
            }

            // MAE/MFE считаются от СРЕДНЕЙ цены входа — от той же, от которой
            // считается результат. Пока MAE мерялась от первой цены, а результат
            // от средней, на входе с доборами MAE могла выйти МЕНЬШЕ убытка,
            // что физически невозможно.
            //
            // Теперь MAE >= |убыток| выполняется по построению: цены всех филлов
            // попадают в бегущие экстремумы, поэтому средняя цена выхода не может
            // оказаться за пределами пройденного диапазона.
            public double MaeTicks(double tick)
            {
                if (tick <= 0)
                    return double.NaN;
                double e = this.AvgEntry;
                double adverse = this.Side == Side.Buy
                    ? e - this.RunMin
                    : this.RunMax - e;
                return Math.Max(0, adverse) / tick;
            }

            public double MfeTicks(double tick)
            {
                if (tick <= 0)
                    return double.NaN;
                double e = this.AvgEntry;
                double favorable = this.Side == Side.Buy
                    ? this.RunMax - e
                    : e - this.RunMin;
                return Math.Max(0, favorable) / tick;
            }
        }

        #endregion

        #region Утилиты

        // Имена полей внутри PnLItem в документации не описаны, а промах
        // в имени — это ошибка компиляции и лишний заход. Читаем через
        // рефлексию: код собирается при любой форме класса, а что именно
        // нашлось, один раз пишется в лог.
        private static readonly Dictionary<string, PropertyInfo> PropCache =
            new Dictionary<string, PropertyInfo>();
        private static readonly string[] MoneyFieldNames =
            { "Value", "Amount", "Money", "Net", "Sum", "Total" };

        private static PropertyInfo FindProp(Type type, string name)
        {
            string key = type.FullName + "::" + name;

            lock (PropCache)
            {
                PropertyInfo pi;
                if (PropCache.TryGetValue(key, out pi))
                    return pi;

                pi = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                PropCache[key] = pi;
                return pi;
            }
        }

        /// <summary>
        /// Денежное значение из свойства объекта. Свойство может быть числом
        /// или обёрткой вроде PnLItem — тогда ищем число внутри неё.
        /// Не нашлось — NaN, и колонка останется пустой.
        /// </summary>
        private double ReadMoney(object owner, string propName)
        {
            try
            {
                if (owner == null)
                    return double.NaN;

                var pi = FindProp(owner.GetType(), propName);
                if (pi == null)
                    return double.NaN;

                object v = pi.GetValue(owner);
                if (v == null)
                    return double.NaN;

                if (v is double dv) return dv;
                if (v is decimal mv) return (double)mv;
                if (v is float fv) return fv;

                foreach (var inner in MoneyFieldNames)
                {
                    var ip = FindProp(v.GetType(), inner);
                    if (ip == null)
                        continue;

                    object iv = ip.GetValue(v);
                    if (iv is double idv) { this.NoteMoneyShape(propName, v.GetType().Name, inner); return idv; }
                    if (iv is decimal imv) { this.NoteMoneyShape(propName, v.GetType().Name, inner); return (double)imv; }
                    if (iv is float ifv) { this.NoteMoneyShape(propName, v.GetType().Name, inner); return ifv; }
                }

                this.NoteMoneyShape(propName, v.GetType().Name, "НЕ НАЙДЕНО");
                return double.NaN;
            }
            catch
            {
                return double.NaN;
            }
        }

        private readonly HashSet<string> reportedShapes = new HashSet<string>();

        private void NoteMoneyShape(string propName, string typeName, string inner)
        {
            string msg = $"Деньги: Trade.{propName} -> {typeName}.{inner}";

            lock (this.reportedShapes)
            {
                if (!this.reportedShapes.Add(msg))
                    return;
            }

            this.Log(msg, StrategyLoggingLevel.Trading);
        }

        private static TimeZoneInfo ResolveNyZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
        }

        // Смещение NY кэшируется на минуту: полный перевод часового пояса
        // заметно дороже сложения, а вызывается он часто
        private static TimeSpan nyOffset;
        private static DateTime nyOffsetValidUntil = DateTime.MinValue;

        private static DateTime ToNy(DateTime utc)
        {
            if (utc >= nyOffsetValidUntil || utc < nyOffsetValidUntil.AddMinutes(-2))
            {
                nyOffset = NyZone.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
                nyOffsetValidUntil = utc.AddMinutes(1);
            }

            return utc + nyOffset;
        }

        private static string Ts(DateTime dt)
            => dt == DateTime.MinValue ? "" : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        /// <summary>MNQU6 -> MNQ. Если шаблон не распознан, возвращается имя как есть.</summary>
        private static string RootOf(string symbolName)
        {
            if (string.IsNullOrEmpty(symbolName))
                return "";

            var m = RootRegex.Match(symbolName);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : symbolName.ToUpperInvariant();
        }

        private double TickSizeOf(Symbol sym)
            => sym != null && sym.TickSize > 0 ? sym.TickSize : 0.25;

        private double TickValueOf(string symbolName)
        {
            double v;
            if (this.tickValues.TryGetValue(RootOf(symbolName), out v))
                return v;
            return this.DefaultTickValue > 0 ? this.DefaultTickValue : 0.5;
        }

        // Числа всегда с точкой: инвариантная культура, без исключений
        private static string Num(double v)
            => double.IsNaN(v) || double.IsInfinity(v) ? "" : v.ToString("0.####", CultureInfo.InvariantCulture);

        private static string Csv(string v)
            => string.IsNullOrEmpty(v) ? "" : "\"" + v.Replace("\"", "\"\"") + "\"";

        #endregion
    }
}
