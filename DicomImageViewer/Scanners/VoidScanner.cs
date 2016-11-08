﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Model;
using Model.Utils;

namespace DicomImageViewer.Scanners
{
    public class VoidScanner
    {
        private readonly IScanData _scanData;
        private readonly Func<ILabelMap> _labelMap;
        private readonly ILookupTable _lookupTable;
        
        public int MaxSkip { get; set; } = 6;
        public ushort thUp { get; set; } = 5;
        public ushort thDown { get; set; } = 5;
        public int Rays { get; set; } = 360;

        public VoidScanner(IScanData scanData, ILookupTable lookupTable, Func<ILabelMap> labelMap)
        {
            _scanData = scanData;
            _labelMap = labelMap;
            _lookupTable = lookupTable;
        }

        public void Build(Point3D point, Axis axis, IProgress progress)
        {
            _labelMap().Reset();
            _labelMap().BuildMethod = BuildMethod.RayCasting;

            var fixProbe = Probe.GetStartingProbe(point, _scanData, _lookupTable, thUp, thDown);

            var heightMap = BuildHeightMap(point, axis, fixProbe);
            var maxHeight = heightMap.Keys.Max();
            var minHeight = heightMap.Keys.Min();

            progress.Min(1);
            progress.Max(maxHeight - minHeight);
            progress.Reset();

            var tasks = new List<Task>();
            tasks.Add(Task.Factory.StartNew(() =>
            {
                Point3D center = point;

                //go up
                for (var h = point[axis]; h <= maxHeight; h++)
                {
                    var p = new Point3D(center)
                    {
                        [axis] = h
                    };

                    center = ScanProjection(p, axis, fixProbe, heightMap);

                    progress.Tick();
                }
            }));


            tasks.Add(Task.Factory.StartNew(() =>
            {
                Point3D center = point;

                ////go down
                for (var h = point[axis] - 1; h >= minHeight; h--)
                {
                    var p = new Point3D(center)
                    {
                        [axis] = h
                    };

                    center = ScanProjection(p, axis, fixProbe, heightMap);

                    progress.Tick();
                }
            }));

            Task.WaitAll(tasks.ToArray());

            CalculateVolume();

            _labelMap().FireUpdate();

            Task.Factory.StartNew(GC.Collect);
        }

        private void CalculateVolume()
        {
            var volume = 0.0d;
            var guard = new object();

            Parallel.ForEach(_labelMap().GetCenters(), d =>
            {
                var proj = _labelMap().GetProjection(Axis.Z, d.Z).ToList();
                proj.Add(proj.Last());

                var area = 0.0d;

                for (int v = 0; v < proj.Count - 1; v++)
                {
                    var a = Math.Sqrt(Math.Pow(proj[v].X - d.X, 2) + Math.Pow(proj[v].Y - d.Y, 2));
                    var b = Math.Sqrt(Math.Pow(proj[v + 1].X - d.X, 2) + Math.Pow(proj[v + 1].Y - d.Y, 2));
                    var c = Math.Sqrt(Math.Pow(proj[v + 1].X - proj[v].X, 2) + Math.Pow(proj[v + 1].Y - proj[v].Y, 2));

                    var s = (a + b + c)/2;

                    area += Math.Sqrt(s*Math.Abs(s - a)*Math.Abs(s - b)*Math.Abs(s - c));
                }

                lock (guard)
                {
                    volume += area;
                }
            });

            double xres, yres, zres;
            _scanData.Resolution(out xres, out yres, out zres);

            _labelMap().Volume = (int)(volume * xres * yres * zres);
        }

        private Point3D ScanProjection(Point3D point, Axis axis, Probe fixProbe, IDictionary<int, Point3D> heightMap)
        {
#if DEBUG
            _labelMap().AddDebugPoint(point);
#endif
            var projection = _scanData.GetProjection(axis, point[axis]);

            if (projection.Empty)
                return point;

            var scalarPoint = point.To2D(axis);
            ushort probe = _lookupTable.Map(projection.Pixels[scalarPoint.X, scalarPoint.Y]);

            if (fixProbe.InRange(probe))
            {
                var layer = RayCasting(point, projection, axis, fixProbe);
                var point3Ds = layer as Point3D[] ?? layer.ToArray();
                var center = CalculateLayerCenter(point3Ds.ToList());
                _labelMap().Add(point3Ds);

                _labelMap().AddCenter(point);

                return center;
            }
            //else
            //{
            //    if (heightMap.ContainsKey(point[axis]))
            //    {
            //        var mark = heightMap[point[axis]];

            //        var layer = RayCasting(mark, projection, axis, fixProbe);

            //        var center = CalculateLayerCenter(layer.ToList());
            //        _labelMap.Add(layer);

            //        _labelMap.AddCenter(mark);

            //        return center;
            //    }

            //    return point;
            //}
            return point;
        }

        private Point3D CalculateLayerCenter(IList<Point3D> layer)
        {
            int x = 0;
            int y = 0;

            foreach(var mark in layer)
            {
                x += mark.X;
                y += mark.Y;
            }

            return new Point3D(x / layer.Count, y / layer.Count, layer.First().Z);
        }
        
        private IEnumerable<Point3D> RayCasting(Point3D point, Projection projection, Axis axis, Probe probe)
        {
            var res = new List<Point3D>();
            
            if (projection.Empty)
                return res;

            var scalarPoint = point.To2D(axis);

            var rayOffset = Math.PI * 2 / Rays;
                        
            for (int r = 0; r < Rays; r++)
            {
                var angle = r * rayOffset;
                var p3d = Cast(scalarPoint, angle, projection, probe).To3D(axis, point[axis]);
                p3d.Index = r;
                res.Add(p3d);
            }

            return res;
        }

        private IDictionary<int, Point3D> BuildHeightMap(Point3D point, Axis origianlAxis, Probe probe)
        {
            var map = new Dictionary<int, Point3D>();

            foreach (Axis axis in Enum.GetValues(typeof(Axis)))
            {
                if (axis != origianlAxis)
                {
                    var projection = _scanData.GetProjection(axis, point[axis]);

                    var marks = RayCasting(point, projection, axis, probe);

                    foreach (var mark in marks)
                    {
                        map[mark[origianlAxis]] = mark;
                        
                        //_labelMap.Add(mark);
                    }
                }
            }

            return map;
        } 

        private Point2D Cast(Point2D point, double angle, Projection projection, Probe refProbe)
        {
            int px = point.X;
            int py = point.Y;

            int skipped = 0;

            for (var l = 0; l < Math.Max(projection.Width, projection.Height); l++)
            {
                px = (int)(point.X + Math.Cos(angle) * l);
                py = (int)(point.Y + Math.Sin(angle) * l);

                bool edge = false;

                if (px < 0)
                {
                    px = 0;
                    edge = true;
                }

                if (py < 0)
                {
                    py = 0;
                    edge = true;
                }

                if (px >= projection.Width)
                {
                    px = projection.Width - 1;
                    edge = true;
                }

                if (py >= projection.Height)
                {
                    py = projection.Height - 1;
                    edge = true;
                }

                if (edge)
                {
                    break;
                }

                var probe = _lookupTable.Map(projection.Pixels[px, py]);
                if (probe >= refProbe.Max || probe <= refProbe.Min)
                {
                    skipped++;
                }
                else
                {
                    skipped = 0;
                }

                if (skipped >= MaxSkip)
                {
                    break;
                }
            }

            return new Point2D() { X = px, Y = py };
        }
    }
}
