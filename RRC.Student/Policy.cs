using System;
using RRC.Abstractions;

namespace RRC.Student
{
    public class Policy : IRequestPolicy
    {
        private readonly object _lock = new();

        // Основные лимиты
        private const int MaxAttempts = 3;
        private const double MinTimeoutMs = 200.0;
        private const double MaxTimeoutMs = 6000.0;

        // Статистика сервера (EMA + Отклонение)
        private double _emaDurationMs = 350.0;
        private double _emaDeviationMs = 100.0; 
        private int _consecutiveTimeouts = 0;

        public TimeSpan GetTimeout(RequestContext context, Random random)
        {
            lock (_lock)
            {
                // Формула оценки диапозона Timeout (p90-p95)
                double baseTimeout = _emaDurationMs + (2.2 * _emaDeviationMs);

                // Если идет серия таймаутов — сервер деградирует, расширяем окно
                if (_consecutiveTimeouts > 3)
                {
                    baseTimeout *= 1.3;
                }

                double clampedMs = Math.Clamp(baseTimeout, MinTimeoutMs, MaxTimeoutMs);
                return TimeSpan.FromMilliseconds(clampedMs);
            }
        }

        public RetryDecision OnResult(RequestObservation observation, Random random)
        {
            lock (_lock)
            {
                // 1. УСПЕШНЫЙ ЗАПРОС (Проверяем через Outcome, обычно это Enum. Успех равен RequestOutcome.Success)
                bool isSuccess = observation.Outcome == RequestOutcome.Success; 

                if (isSuccess)
                {
                    // сброс счетчика Timeout
                    _consecutiveTimeouts = 0;

                    // Обновляем среднее время и отклонение (EMA)
                    double duration = observation.Elapsed.TotalMilliseconds;
                    double delta = Math.Abs(duration - _emaDurationMs);
                    // вес ноновых коэф. 0.2, старых 0.8
                    _emaDurationMs = (0.2 * duration) + (0.8 * _emaDurationMs);
                    _emaDeviationMs = (0.2 * delta) + (0.8 * _emaDeviationMs);

                    return RetryDecision.NoRetry();
                }

                // 2. ПРЕВЫШЕНИЕ ЧИСЛА ПОПЫТОК
                if (observation.Attempt >= MaxAttempts)
                {
                    return RetryDecision.NoRetry();
                }

                // Проверяем, был ли это таймаут (обычно RequestOutcome.Timeout)
                bool isTimeout = observation.Outcome == RequestOutcome.Timeout;

                if (isTimeout)
                {
                    _consecutiveTimeouts++;
                }

                // 3. РАСЧЕТ ЗАДЕРЖКИ (BACKOFF)
                double baseDelayMs = isTimeout ? 300.0 : 150.0;

                // Экспоненциальный рост в зависимости от номера попытки
                baseDelayMs *= Math.Pow(2.2, observation.Attempt - 1);

                // Дополнительный штраф при серии таймаутов 
                if (_consecutiveTimeouts > 3)
                {
                    baseDelayMs *= 1.5;
                }

                //  распределение паузы от 50% до 150% от расчетной
                double jitter = 0.5 + random.NextDouble();
                double finalDelayMs = baseDelayMs * jitter;

                finalDelayMs = Math.Clamp(finalDelayMs, 50.0, 4000.0);

                return RetryDecision.RetryAfter(TimeSpan.FromMilliseconds(finalDelayMs));
            }
        }
    }
}
