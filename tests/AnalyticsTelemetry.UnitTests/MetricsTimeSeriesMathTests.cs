using AnalyticsTelemetry.Telemetry;
using Godot;
using Xunit;

namespace AnalyticsTelemetry.UnitTests;

public sealed class MetricsTimeSeriesMathTests
{
    private static MetricTimeSeries Ts(string title, params double[] values) =>
        new(title, Colors.White, values);

    public sealed class ChartSampleCountTests
    {
        [Fact]
        public void Empty_series_list_yields_minimum_two()
        {
            Assert.Equal(2, MetricsTimeSeriesMath.ChartSampleCount(Array.Empty<MetricTimeSeries>()));
        }

        [Fact]
        public void Single_point_series_yields_two()
        {
            var series = new[] { Ts("a", 1.0) };
            Assert.Equal(2, MetricsTimeSeriesMath.ChartSampleCount(series));
        }

        [Fact]
        public void Uses_max_length_across_series()
        {
            var series = new[]
            {
                Ts("a", 1, 2),
                Ts("b", 1, 2, 3, 4, 5),
            };
            Assert.Equal(5, MetricsTimeSeriesMath.ChartSampleCount(series));
        }

        [Fact]
        public void Two_or_more_points_uses_raw_length()
        {
            var series = new[] { Ts("a", 10.0, 20.0, 30.0) };
            Assert.Equal(3, MetricsTimeSeriesMath.ChartSampleCount(series));
        }
    }

    public sealed class InterpolateAtChartIndexTests
    {
        [Fact]
        public void Empty_values_returns_nan()
        {
            var values = Array.Empty<double>();
            Assert.True(double.IsNaN(MetricsTimeSeriesMath.InterpolateAtChartIndex(values, 2, 0)));
        }

        [Fact]
        public void Non_positive_n_returns_nan()
        {
            Assert.True(double.IsNaN(MetricsTimeSeriesMath.InterpolateAtChartIndex(new[] { 1.0 }, 0, 0)));
        }

        [Fact]
        public void Single_chart_index_returns_first_value()
        {
            Assert.Equal(42.0, MetricsTimeSeriesMath.InterpolateAtChartIndex(new[] { 42.0 }, 1, 0));
            Assert.Equal(42.0, MetricsTimeSeriesMath.InterpolateAtChartIndex(new[] { 42.0 }, 1, 0.25));
        }

        [Fact]
        public void Endpoints_match_stored_values()
        {
            var v = new[] { 1.0, 9.0 };
            var n = 2;
            Assert.Equal(1.0, MetricsTimeSeriesMath.InterpolateAtChartIndex(v, n, 0));
            Assert.Equal(9.0, MetricsTimeSeriesMath.InterpolateAtChartIndex(v, n, 1));
        }

        [Fact]
        public void Midpoint_linear_interpolation()
        {
            var v = new[] { 0.0, 10.0 };
            var n = 2;
            Assert.Equal(5.0, MetricsTimeSeriesMath.InterpolateAtChartIndex(v, n, 0.5));
        }

        [Fact]
        public void T_clamped_to_chart_range()
        {
            var v = new[] { 100.0, 200.0 };
            var n = 2;
            Assert.Equal(100.0, MetricsTimeSeriesMath.InterpolateAtChartIndex(v, n, -50));
            Assert.Equal(200.0, MetricsTimeSeriesMath.InterpolateAtChartIndex(v, n, 99));
        }

        [Fact]
        public void When_n_exceeds_value_count_tail_extends_last_sample()
        {
            var v = new[] { 1.0, 2.0, 99.0 };
            var n = 5;
            Assert.Equal(99.0, MetricsTimeSeriesMath.InterpolateAtChartIndex(v, n, 4));
            Assert.Equal(99.0, MetricsTimeSeriesMath.InterpolateAtChartIndex(v, n, 3.5));
        }

        [Fact]
        public void Interpolation_spans_gap_where_tail_is_flat()
        {
            var v = new[] { 10.0, 20.0, 30.0 };
            var n = 5;
            var mid = MetricsTimeSeriesMath.InterpolateAtChartIndex(v, n, 1.5);
            Assert.Equal(25.0, mid, precision: 10);
        }
    }

    public sealed class SeriesWindowMaxTests
    {
        [Fact]
        public void ComputeSeriesWindowMax_finds_largest_value()
        {
            Assert.Equal(12.0, MetricsTimeSeriesMath.ComputeSeriesWindowMax(new[] { 1.0, 5.0, 12.0, 3.0 }));
        }

        [Fact]
        public void ComputeSeriesWindowMax_empty_is_zero()
        {
            Assert.Equal(0.0, MetricsTimeSeriesMath.ComputeSeriesWindowMax(Array.Empty<double>()));
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1e-7, 1)]
        [InlineData(3, 3)]
        public void ChartNormalizeDenominator_matches_renderer_epsilon(double dataMax, double expected)
        {
            Assert.Equal(expected, MetricsTimeSeriesMath.ChartNormalizeDenominator(dataMax));
        }
    }

    public sealed class SharedPerChartYScaleTests
    {
        [Fact]
        public void ComputeSharedSeriesDataMax_is_max_across_all_lines_on_that_chart()
        {
            var series = new[]
            {
                Ts("a", 1, 5),
                Ts("b", 3, 4, 12),
            };
            Assert.Equal(12.0, MetricsTimeSeriesMath.ComputeSharedSeriesDataMax(series));
        }

        [Fact]
        public void ComputeSharedSeriesDataMax_empty_series_list_is_zero()
        {
            Assert.Equal(0.0, MetricsTimeSeriesMath.ComputeSharedSeriesDataMax(Array.Empty<MetricTimeSeries>()));
        }
    }
}
