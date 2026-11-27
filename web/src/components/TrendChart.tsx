import { useEffect, useRef } from "react";
import * as echarts from "echarts";

// backlog D2：网站统计趋势图组件——按日 PV/UV 折线（echarts SVG 渲染，同 Metrics 页口径）。
export interface TrendPoint {
  date: string;
  pv: number;
  uv: number;
}

export default function TrendChart({ points }: { points: TrendPoint[] }) {
  const chartRef = useRef<HTMLDivElement | null>(null);
  const chart = useRef<echarts.ECharts | null>(null);

  useEffect(() => {
    if (!chartRef.current) return;
    chart.current = echarts.init(chartRef.current, undefined, { renderer: "svg" });
    const onResize = () => chart.current?.resize();
    window.addEventListener("resize", onResize);
    return () => {
      window.removeEventListener("resize", onResize);
      chart.current?.dispose();
      chart.current = null;
    };
  }, []);

  useEffect(() => {
    if (!chart.current) return;
    chart.current.setOption({
      tooltip: { trigger: "axis" },
      legend: { data: ["PV", "UV（估算）"] },
      grid: { left: 48, right: 24, top: 36, bottom: 28 },
      xAxis: { type: "category", data: points.map((p) => p.date) },
      yAxis: { type: "value", minInterval: 1 },
      series: [
        { name: "PV", type: "line", smooth: true, data: points.map((p) => p.pv) },
        { name: "UV（估算）", type: "line", smooth: true, data: points.map((p) => p.uv) },
      ],
    });
  }, [points]);

  return <div ref={chartRef} style={{ width: "100%", height: 280 }} />;
}
