﻿using Algorithms.Graphs;
using DataStructures.Graphs;
using Domain.Base.Demand.Reservation;
using Domain.Base.Network.RailwayBasicNetwork;
using Domain.Base.Schedule.Timetable;
using ILOG.Concert;
using ILOG.CPLEX;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace Domain.Calculation.Optimization.DPN
{
    /// <summary>
    /// Cutting Plane Method with Trust Region
    /// </summary>
    [DisplayName("DPNv4")]
    public class DpnSolverV4 : AbsDpnSolver
    {
        public DpnSolverV4(DPNProblemContext ctx) : base(ctx) { }

        public decimal ObjValue;

        public override void Work()
        {
            //获取求解设置
            decimal terminalFactor = _ctx.GetParameter("TerminalFactor", 0.001m);
            int iteration = _ctx.GetParameter("Iteration", 10);
            int resolution = _ctx.GetParameter("Resolution", 60);
            decimal initMultipler = _ctx.GetParameter("InitMultiper", 0.1m);

            string objectiveType = _ctx.GetParameter("ObjectiveType", "");

            // 相对-绝对时间转化器
            DiscreteTimeAdapter adapter
                = new DiscreteTimeAdapter(_ctx.StartTime, _ctx.EndTime, resolution);

            // 路径集
            Dictionary<CustomerArrival, List<TravelPath>> pathDict
                = new Dictionary<CustomerArrival, List<TravelPath>>();

            #region 建立初始网络，搜索可行路径
            //目标函数网络
            var objgraph = ObjectNetworkFactory.Create(objectiveType, _ctx, adapter); //new ObjectTravelHyperNetwork(_ctx, adapter);
            objgraph.Build();

            //基础网络
            var basicGraph = new BasicTravelHyperNetwork(_ctx, adapter);
            basicGraph.Build();

            SubTasks.Clear();
            foreach (CustomerArrival customer in _ctx.Pal)
            {
                Task ta = factory.StartNew(() =>
                {
                    var ori = (_ctx.Wor.Mar[customer.Customer.MarSegID] as IRailwayMarketSegment).OriSta;
                    var des = (_ctx.Wor.Mar[customer.Customer.MarSegID] as IRailwayMarketSegment).DesSta;
                    var paths = DepthFirstSearcher.FindAllPaths(basicGraph,
                        new TravelHyperNode() { Time = adapter.ConvertToDiscreteTime(customer.ArriveTime), Station = ori, Price = 0 },
                        new TravelHyperNode() { Time = adapter.Horizon + 1440, Station = des, Price = 0 });
                    pathDict.Add(customer, new List<TravelPath>());
                    foreach (var path in paths)
                    {
                        pathDict[customer].Add(new TravelPath(basicGraph, path));
                    }
                });
                SubTasks.Add(ta);
            }
            Task.WaitAll(SubTasks.ToArray());
            #endregion

            #region 构建对偶问题 with CPLEX
            Cplex model = new Cplex();
            model.SetOut(null);
            INumVar theta = model.NumVar(double.MinValue, double.MaxValue); //θ

            model.AddMaximize(theta);

            Dictionary<IServiceSegment, INumVar> dual_rho = _ctx.Wor.RailwayTimeTable.Trains
                .SelectMany(i => i.ServiceSegments).ToDictionary(i => i, i =>
                    model.NumVar(0, double.MaxValue));

            Dictionary<CustomerArrival, Dictionary<TravelPath, INumVar>> dual_mu =
                new Dictionary<CustomerArrival, Dictionary<TravelPath, INumVar>>();
            foreach (CustomerArrival customer in _ctx.Pal)
            {
                dual_mu.Add(customer, new Dictionary<TravelPath, INumVar>());
                foreach (var path in pathDict[customer])
                {
                    dual_mu[customer].Add(path, model.NumVar(0, double.MaxValue));
                }
            }

            Dictionary<IEdge<TravelHyperNode>, INumVar> dual_lambda =
                new Dictionary<IEdge<TravelHyperNode>, INumVar>();
            foreach (CustomerArrival customer in _ctx.Pal)
            {
                foreach (var path in pathDict[customer])
                {
                    if (!dual_lambda.ContainsKey(path.ReservationArc))
                    {
                        dual_lambda.Add(path.ReservationArc, model.NumVar(0, double.MaxValue));
                    }
                }
            }
            #endregion

            #region 变量与乘子
            //决策变量 x
            Dictionary<CustomerArrival, TravelPath> x
               = new Dictionary<CustomerArrival, TravelPath>();
            foreach (CustomerArrival customer in _ctx.Pal)
            {
                x.Add(customer, new TravelPath());
            }

            //决策变量 w
            Dictionary<ITrainTrip, Dictionary<IRailwayStation, PricePath>> w
                = new Dictionary<ITrainTrip, Dictionary<IRailwayStation, PricePath>>();
            foreach (var train in _ctx.Wor.RailwayTimeTable.Trains)
            {
                w.Add(train, new Dictionary<IRailwayStation, PricePath>());
                foreach (var sta in _ctx.Wor.Net.StationCollection)
                {
                    w[train].Add(sta, null);
                }
            }

            //辅助变量 y
            //记录每条弧在当前w的取值下是否可行(available)，值为true = 可行；false = 不可行
            //超出了y记录的reservation arc 不会有人走
            Dictionary<IEdge<TravelHyperNode>, bool> y
                = new Dictionary<IEdge<TravelHyperNode>, bool>();
            foreach (var p in pathDict.Values.SelectMany(i => i))
            {
                if (p.ReservationArc != null && !y.ContainsKey(p.ReservationArc))
                    y.Add(p.ReservationArc, false);
            }

            //拉格朗日乘子 rho
            Dictionary<IServiceSegment, decimal> LM_rho = _ctx.Wor.RailwayTimeTable.Trains
            .SelectMany(i => i.ServiceSegments).ToDictionary(i => i, i => initMultipler);

            //拉格朗日乘子 rho 迭代方向
            Dictionary<IServiceSegment, decimal> Grad_rho = _ctx.Wor.RailwayTimeTable.Trains
            .SelectMany(i => i.ServiceSegments).ToDictionary(i => i, i => initMultipler);


            //拉格朗日乘子 mu
            Dictionary<CustomerArrival, Dictionary<TravelPath, decimal>> LM_mu
                = new Dictionary<CustomerArrival, Dictionary<TravelPath, decimal>>();
            foreach (CustomerArrival customer in _ctx.Pal)
            {
                LM_mu.Add(customer, new Dictionary<TravelPath, decimal>());
                foreach (var path in pathDict[customer])
                {
                    LM_mu[customer].Add(path, initMultipler);
                }
            }
            //拉格朗日乘子 mu 迭代方向
            Dictionary<CustomerArrival, Dictionary<TravelPath, decimal>> Grad_mu
                = new Dictionary<CustomerArrival, Dictionary<TravelPath, decimal>>();
            foreach (CustomerArrival customer in _ctx.Pal)
            {
                Grad_mu.Add(customer, new Dictionary<TravelPath, decimal>());
                foreach (var path in pathDict[customer])
                {
                    Grad_mu[customer].Add(path, initMultipler);
                }
            }

            //拉格朗日乘子 lambda
            Dictionary<IEdge<TravelHyperNode>, decimal> LM_lambda = new Dictionary<IEdge<TravelHyperNode>, decimal>();
            //WARNING: 这里缺少了没有旅客选择的reservation arc
            foreach (CustomerArrival customer in _ctx.Pal)
            {
                foreach (var path in pathDict[customer])
                {
                    if (!LM_lambda.ContainsKey(path.ReservationArc))
                    {
                        LM_lambda.Add(path.ReservationArc, initMultipler);
                    }
                }
            }

            //拉格朗日乘子 lambda 迭代方向
            Dictionary<IEdge<TravelHyperNode>, decimal> Grad_lambda = new Dictionary<IEdge<TravelHyperNode>, decimal>();
            foreach (CustomerArrival customer in _ctx.Pal)
            {
                foreach (var path in pathDict[customer])
                {
                    if (!Grad_lambda.ContainsKey(path.ReservationArc))
                    {
                        Grad_lambda.Add(path.ReservationArc, initMultipler);
                    }
                }
            }

            #endregion 

            decimal bigM1 = pathDict.Max(i => i.Value.Max(j => basicGraph.GetPathCost(j)));
            decimal bigM2 = _ctx.Pal.Count();
            decimal bigM = Math.Max(bigM1, bigM2);
            decimal lowerBound = decimal.MinValue;
            decimal upperBound = decimal.MaxValue;
            decimal trustRegion = 1m;
            decimal LagErrBound = 0.01m;

            bool flag = false;//对偶问题有解
            decimal lastlowerBound = 0;

            PrintIterationInfo($"Iteration Number, Lower Bound, Upper Bound, Best Lower Bound, Best Upper Bound, Total Gap(%) ");

            for (int iter = 0; iter < iteration; iter++)
            {
                Log($"--------------第{iter}轮求解开始--------------");

                bool hasFeasibleSolution = true;

                #region 求解LR问题
                SubTasks.Clear();
                foreach (CustomerArrival customer in _ctx.Pal)// 求解x
                {
                    Task ta = factory.StartNew(() =>
                    {
                        var graph = new LRxTravelHyperNetwork(_ctx, adapter, objgraph, customer, pathDict, LM_rho, LM_mu, LM_lambda);
                        graph.Build();
                        var ori = (_ctx.Wor.Mar[customer.Customer.MarSegID] as IRailwayMarketSegment).OriSta;
                        var des = (_ctx.Wor.Mar[customer.Customer.MarSegID] as IRailwayMarketSegment).DesSta;
                        DijkstraShortestPaths<DirectedWeightedSparseGraph<TravelHyperNode>, TravelHyperNode> dijkstra
                            = new DijkstraShortestPaths<DirectedWeightedSparseGraph<TravelHyperNode>, TravelHyperNode>(graph, new TravelHyperNode()
                            {
                                Time = adapter.ConvertToDiscreteTime(customer.ArriveTime),
                                Station = ori,
                                Price = 0
                            });//考虑该旅客到达时间
                        x[customer] = new TravelPath(graph, dijkstra.ShortestPathTo(
                            new TravelHyperNode()
                            {
                                Time = adapter.Horizon + 1440,
                                Station = des,
                                Price = 0
                            }));
                    });
                    SubTasks.Add(ta);
                }
                foreach (var train in _ctx.Wor.RailwayTimeTable.Trains)
                {
                    foreach (IRailwayStation station in _ctx.Wor.Net.StationCollection)// 求解w       
                    {
                        Task ta = factory.StartNew(() =>
                        {
                            var graph = DpnAlgorithm.BuildLRwGraph(_ctx, adapter, train, station, pathDict, basicGraph.LinkTrainDict, LM_mu, LM_lambda);
                            DijkstraShortestPaths<DirectedWeightedSparseGraph<string>, string> dijkstra
                                = new DijkstraShortestPaths<DirectedWeightedSparseGraph<string>, string>(graph, "Start");//考虑该旅客到达时间
                            var nodepath = dijkstra.ShortestPathTo("End");
                            if (nodepath == null)
                            {
                                throw new System.Exception("No path found");
                            }
                            else
                            {
                                w[train][station] = new PricePath(graph, nodepath);
                            }

                        });
                        SubTasks.Add(ta);
                    }
                }
                Task.WaitAll(SubTasks.ToArray());

                foreach (var edge in y.Keys.ToArray())//更新y
                {
                    var sta = edge.Source.Station;
                    var train = basicGraph.GetTrainByReservationLink(edge);
                    y[edge] = w[train][sta].GetWrapPoints(edge.Destination.Price, edge.Source.Time).Any();
                }
                #endregion

                #region 计算拉格朗日函数值作为下界

                decimal templowerBound = 0m;
                decimal templowerBound_part1 = 0m;
                decimal templowerBound_part2 = 0m;
                decimal templowerBound_part3 = 0m;
                decimal templowerBound_part4 = 0m;
                Dictionary<CustomerArrival, decimal> lbValueDic
                    = new Dictionary<CustomerArrival, decimal>();
                //1 计算在基础网络中的路径cost
                foreach (CustomerArrival customer in _ctx.Pal)
                {
                    lbValueDic.Add(customer, objgraph.GetPathCost(x[customer]));
                    templowerBound_part1 += lbValueDic[customer];
                }

                //2计算BRUE项
                templowerBound_part2 += _ctx.Pal.Sum(c => pathDict[c].Sum(p =>
                {
                    decimal secondItem = 0m;
                    secondItem += basicGraph.GetPathCost(x[c]) - basicGraph.GetPathCost(p) - _ctx.SitaDic[c.Customer.MarSegID];
                    secondItem -= (p.ReservationArc != null && y[p.ReservationArc]) ? 0 : bigM;
                    return secondItem * LM_mu[c][p];
                }));

                //3计算In-train Capacity项
                Dictionary<IServiceSegment, int> ServiceDic = _ctx.Wor.RailwayTimeTable
                    .Trains.SelectMany(i => i.ServiceSegments)
                    .ToDictionary(i => i, i => 0);//当前service segment使用情况

                foreach (var p in x.Values)
                {
                    foreach (IServiceSegment seg in p.GetSegments(basicGraph))
                    {
                        ServiceDic[seg] += 1;
                    }
                }
                foreach (var train in _ctx.Wor.RailwayTimeTable.Trains)
                {
                    foreach (var seg in train.ServiceSegments)
                    {
                        templowerBound_part3 += LM_rho[seg] * (ServiceDic[seg] - train.Carriage.Chairs.Count());
                    }
                }

                //4 计算reservation constraint 项
                Dictionary<IEdge<TravelHyperNode>, int> reservationDic
                    = new Dictionary<IEdge<TravelHyperNode>, int>();
                foreach (var p in x.Values)
                {
                    if (reservationDic.ContainsKey(p.ReservationArc))
                    {
                        reservationDic[p.ReservationArc] += 1;
                    }
                    else
                    {
                        reservationDic.Add(p.ReservationArc, 1);
                    }
                }
                foreach (var pair in y.Keys)
                {
                    //y是所有的reservation 的集合 reservationDic 是已经使用的reservation 集合
                    var res = reservationDic.Keys.FirstOrDefault(i => i.Source == pair.Source && i.Destination == pair.Destination);
                    templowerBound_part4 += LM_lambda[pair] * ((res != null ? reservationDic[res] : 0) - (y[pair] ? bigM : 0));
                }

                templowerBound = templowerBound_part1 + templowerBound_part2 + templowerBound_part3 + templowerBound_part4;

                //Log($"Lower Bound = { Math.Round(templowerBound, 2)}," +
                //   $"({ Math.Round(templowerBound_part1, 2) }" +
                //   $"+{ Math.Round(templowerBound_part2, 2)}" +
                //   $"+{ Math.Round(templowerBound_part3, 2)}" +
                //   $"+{ Math.Round(templowerBound_part4, 2)})");

                PrintLBSolution(DpnAlgorithm.GetTravelPathString(_ctx, adapter, objgraph, x, lbValueDic));
                #endregion

                #region 乘子更新
                if (flag)
                {
                    decimal LagLowerBound = Convert.ToDecimal(model.GetValue(theta));
                    lowerBound = Math.Max(lowerBound, templowerBound);
                    decimal lagGap = LagLowerBound - templowerBound;

                    Log($"Lower Bound = { templowerBound }");
                    Log($"Lag Lower Bound = { LagLowerBound }");
                    if (lagGap <= LagErrBound)//判断拉格朗日对偶问题是否达到最优
                    {
                        Log($"求解终止：对偶函数已最优。");
                        break;
                    }
                    else
                    {
                        decimal ratio = (templowerBound - lastlowerBound) / lagGap;
                        if (ratio >= 1m)
                        {
                            trustRegion = trustRegion * 2m;
                        }
                        else if (ratio < 0)
                        {
                            trustRegion = trustRegion / 3m;
                        }
                        if (ratio >=0m)
                        {
                            /* 更新乘子值 */
                            foreach (var pair in dual_rho)
                            {
                                LM_rho[pair.Key] = Convert.ToDecimal(model.GetValue(pair.Value));
                            }
                            foreach (var pair in dual_lambda)
                            {
                                LM_lambda[pair.Key] = Convert.ToDecimal(model.GetValue(pair.Value));
                            }
                            foreach (CustomerArrival customer in _ctx.Pal)
                            {
                                foreach (var path in pathDict[customer])
                                {
                                    LM_mu[customer][path] = Convert.ToDecimal(model.GetValue(dual_mu[customer][path]));
                                }
                            }
                        }
                        Log($"ratio = { ratio }, Trust-Region = { trustRegion }");
                    }
                }
                else /* 如果对偶问题无可行解，不迭代 */
                {
                    //decimal step = 1.618m / (iter + 1);
                    //foreach (CustomerArrival c in _ctx.Pal)//更新mu
                    //{
                    //    foreach (TravelPath p in pathDict[c])
                    //    {
                    //        Grad_mu[c][p] = basicGraph.GetPathCost(x[c]) - basicGraph.GetPathCost(p) - _ctx.SitaDic[c.Customer.MarSegID]
                    //            - ((p.ReservationArc != null && y[p.ReservationArc]) ? 0 : bigM);
                    //        LM_mu[c][p] = Math.Max(0, LM_mu[c][p] + step * Grad_mu[c][p]);
                    //    }
                    //}
                    //foreach (var pair in y.Keys) //更新lambda
                    //{
                    //    var res = reservationDic.Keys.FirstOrDefault(i => i.Source == pair.Source && i.Destination == pair.Destination);
                    //    Grad_lambda[pair] = ((res != null ? reservationDic[res] : 0) - (y[pair] ? bigM : 0));
                    //    LM_lambda[pair] = Math.Max(0, LM_lambda[pair] + step * Grad_lambda[pair]);
                    //}
                    //foreach (var train in _ctx.Wor.RailwayTimeTable.Trains)//更新rho
                    //{
                    //    foreach (var seg in train.ServiceSegments)
                    //    {
                    //        Grad_rho[seg] = ServiceDic[seg] - train.Carriage.Chairs.Count();
                    //        LM_rho[seg] = Math.Max(0, LM_rho[seg] + step * Grad_rho[seg]);
                    //    }
                    //}
                }

                lastlowerBound = templowerBound;

                #endregion

                #region 求解拉格朗日对偶问题

                /* 求解 几何乘子 */
                // 增加 一个约束
                INumExpr exp = model.NumExpr();

                //2 计算BRUE项
                foreach (var c in _ctx.Pal)
                {
                    foreach (var p in pathDict[c])
                    {
                        decimal secondItem = 0m;
                        secondItem += basicGraph.GetPathCost(x[c]) - basicGraph.GetPathCost(p) - _ctx.SitaDic[c.Customer.MarSegID];
                        secondItem -= (p.ReservationArc != null && y[p.ReservationArc]) ? 0 : bigM;
                        exp = model.Sum(exp, model.Prod(Convert.ToDouble(secondItem), dual_mu[c][p]));
                    }
                }

                //3计算In-train Capacity项 (这里直接用了计算下界时候的 Service_dic
                foreach (var train in _ctx.Wor.RailwayTimeTable.Trains)
                {
                    foreach (var seg in train.ServiceSegments)
                    {
                        exp = model.Sum(exp, model.Prod(Convert.ToDouble(ServiceDic[seg]
                            - train.Carriage.Chairs.Count()), dual_rho[seg]));
                    }
                }

                //4 计算reservation constraint 项 (这里直接用了计算下界时候的 reservationDic
                foreach (var pair in y.Keys)
                {
                    var res = reservationDic.Keys.FirstOrDefault(i => i.Source == pair.Source
                        && i.Destination == pair.Destination);
                    exp = model.Sum(exp, model.Prod(Convert.ToDouble(((res != null ? reservationDic[res] : 0)
                        - (y[pair] ? bigM : 0))), dual_lambda[pair]));
                }

                model.AddGe(model.Sum(Convert.ToDouble(templowerBound_part1), exp), theta);

                /* Trust-Region */

                foreach (var c in _ctx.Pal)
                {
                    foreach (var p in pathDict[c])
                    {
                        dual_mu[c][p].LB = Math.Max(0, Convert.ToDouble(LM_mu[c][p] - trustRegion));
                        dual_mu[c][p].UB = Convert.ToDouble(LM_mu[c][p] + trustRegion);
                    }
                }

                foreach (var train in _ctx.Wor.RailwayTimeTable.Trains)
                {
                    foreach (var seg in train.ServiceSegments)
                    {
                        dual_rho[seg].LB = Math.Max(0,Convert.ToDouble(LM_rho[seg] - trustRegion));
                        dual_rho[seg].UB = Convert.ToDouble(LM_rho[seg] + trustRegion);
                    }
                }

                foreach (var pair in y.Keys)
                {
                    dual_lambda[pair].LB = Math.Max(0, Convert.ToDouble(LM_lambda[pair] - trustRegion));
                    dual_lambda[pair].UB = Convert.ToDouble(LM_lambda[pair] + trustRegion);
                }

                flag = model.Solve();
                Log($"Is Dual Problem Feasible: { flag }");

                #endregion

                #region 通过一个启发式规则计算上界(按照w模拟到达)

                var pathcost = lbValueDic.ToDictionary(i => i.Key, i => i.Value);
                var x_least = x.ToDictionary(i => i.Key, i => i.Value);//当前w下每个旅客的最短路径
                var x_upperbound = x.ToDictionary(i => i.Key, i => i.Value);
                var x_controlled = x.ToDictionary(i => i.Key, i => i.Value);

                #region 1-构建当前y下的最优x值
                SubTasks.Clear();
                foreach (CustomerArrival customer in _ctx.Pal)// 求解x
                {
                    Task ta = factory.StartNew(() =>
                    {
                        var controlledLRxgraph = new ControlledLRxTravelHyperNetwork(
                            _ctx, adapter, objgraph, customer, pathDict, LM_rho, LM_mu, LM_lambda, y);
                        controlledLRxgraph.Build();
                        var ori = (_ctx.Wor.Mar[customer.Customer.MarSegID] as IRailwayMarketSegment).OriSta;
                        var des = (_ctx.Wor.Mar[customer.Customer.MarSegID] as IRailwayMarketSegment).DesSta;

                        TravelHyperNode startNode = new TravelHyperNode()
                        {
                            Time = adapter.ConvertToDiscreteTime(customer.ArriveTime),
                            Station = ori,
                            Price = 0
                        };
                        TravelHyperNode endNode = new TravelHyperNode()
                        {
                            Time = adapter.Horizon + 1440,
                            Station = des,
                            Price = 0
                        };

                        DijkstraShortestPaths<DirectedWeightedSparseGraph<TravelHyperNode>, TravelHyperNode> dijkstra
                            = new DijkstraShortestPaths<DirectedWeightedSparseGraph<TravelHyperNode>, TravelHyperNode>
                            (controlledLRxgraph, startNode);//考虑该旅客到达时间

                        if (!dijkstra.HasPathTo(endNode))
                        {
                            throw new System.Exception("没有路径!");
                        }
                        else
                        {
                            x_controlled[customer] = new TravelPath(controlledLRxgraph, dijkstra.ShortestPathTo(endNode));
                        }
                    });
                    SubTasks.Add(ta);
                }
                Task.WaitAll(SubTasks.ToArray());
                #endregion

                # region 2-构建当前y下的出行最小值
                var solutiongraph = new ControlledTravelHyperNetwork(_ctx, adapter, y);
                solutiongraph.Build();
                Parallel.ForEach(_ctx.Pal, customer =>                //foreach (var customer in _ctx.Pal)//求此网络下每个旅客的最短路径
                {
                    var ori = (_ctx.Wor.Mar[customer.Customer.MarSegID] as IRailwayMarketSegment).OriSta;
                    var des = (_ctx.Wor.Mar[customer.Customer.MarSegID] as IRailwayMarketSegment).DesSta;

                    TravelHyperNode startNode = new TravelHyperNode()
                    {
                        Time = adapter.ConvertToDiscreteTime(customer.ArriveTime),
                        Station = ori,
                        Price = 0
                    };

                    TravelHyperNode endNode = new TravelHyperNode()
                    {
                        Time = adapter.Horizon + 1440,
                        Station = des,
                        Price = 0
                    };

                    DijkstraShortestPaths<DirectedWeightedSparseGraph<TravelHyperNode>, TravelHyperNode> dijkstra
                       = new DijkstraShortestPaths<DirectedWeightedSparseGraph<TravelHyperNode>, TravelHyperNode>
                       (solutiongraph, startNode);

                    if (!dijkstra.HasPathTo(endNode))
                    {
                        throw new System.Exception("没有路径!");
                    }
                    else
                    {
                        x_least[customer] = new TravelPath(solutiongraph, dijkstra.ShortestPathTo(endNode));
                    }
                });
                #endregion

                #region 3-修复可行解
                var solutiongraphTemp = new SimNetwork(_ctx, adapter, y);//建立仿真网络
                solutiongraphTemp.Build();
                foreach (var customer in _ctx.Pal)
                {
                    x_upperbound[customer] = x_controlled[customer];
                    TravelPath path = x_controlled[customer];

                    if (!solutiongraphTemp.IsPathFeasible(path) ||
                       solutiongraphTemp.GetPathCost(path) > solutiongraph.GetPathCost(x_least[customer]) + _ctx.SitaDic[customer.Customer.MarSegID])//如果违反了容量约束或者BRUE约束
                    {
                        var ori = (_ctx.Wor.Mar[customer.Customer.MarSegID] as IRailwayMarketSegment).OriSta;
                        var des = (_ctx.Wor.Mar[customer.Customer.MarSegID] as IRailwayMarketSegment).DesSta;
                        DijkstraShortestPaths<DirectedWeightedSparseGraph<TravelHyperNode>, TravelHyperNode> dijkstra
                           = new DijkstraShortestPaths<DirectedWeightedSparseGraph<TravelHyperNode>, TravelHyperNode>(solutiongraphTemp, new TravelHyperNode()
                           {
                               Time = adapter.ConvertToDiscreteTime(customer.ArriveTime),
                               Station = ori,
                               Price = 0
                           });

                        //重新查找路径，如果存在路径
                        if (dijkstra.HasPathTo(new TravelHyperNode()
                        {
                            Time = adapter.Horizon + 1440,
                            Station = des,
                            Price = 0
                        }))
                        {
                            x_upperbound[customer] = new TravelPath(solutiongraphTemp, dijkstra.ShortestPathTo(new TravelHyperNode()
                            {
                                Time = adapter.Horizon + 1440,
                                Station = des,
                                Price = 0
                            }));
                            if (solutiongraphTemp.GetPathCost(x_upperbound[customer]) <= //满足BRUE约束
                                solutiongraph.GetPathCost(x_least[customer]) + _ctx.SitaDic[customer.Customer.MarSegID])
                            {
                                path = x_upperbound[customer];
                            }
                            else
                            {
                                hasFeasibleSolution = false;
                                break;
                            }
                        }
                        else
                        {
                            hasFeasibleSolution = false;
                            break;
                        }
                    }

                    pathcost[customer] = objgraph.GetPathCost(path);

                    //加载路径
                    foreach (var seg in path.GetSegments(basicGraph))
                    {
                        solutiongraphTemp.AddUsage(seg, 1);
                    }
                }
                var tempUpperbound = _ctx.Pal.Sum(c => objgraph.GetPathCost(x_upperbound[c]));
                #endregion

                //如果有最优解再更新上界
                bool hasBetterUpperbound = tempUpperbound < upperBound;
                if (hasFeasibleSolution) upperBound = Math.Min(upperBound, tempUpperbound);
                Log($"Upper Bound = {  Math.Round(tempUpperbound, 2) },找到可行解 : { hasFeasibleSolution.ToString()}");
                #endregion

                #region  Gap 信息

                decimal absoluteGap = 0;
                string gapStr = "";
                //如果上限是无穷，那么此时gap也是无穷
                if (upperBound == decimal.MaxValue || lowerBound == decimal.MinValue)
                {
                    absoluteGap = decimal.MaxValue;
                    gapStr = $"+∞";
                }
                else
                {
                    absoluteGap = upperBound - lowerBound;
                    gapStr = $"{ Math.Round(absoluteGap, 2)}";
                }


                if (absoluteGap < terminalFactor && absoluteGap > 0)
                {
                    Log($"求解终止：Gap以满足终止条件，Gap={ absoluteGap }");
                    break;
                }

                Log($"Total Gap = { gapStr }");
                #endregion

                #region 输出信息

                //SendMessage($"#Iteration Number, Lower Bound, Upper Bound, Best Lower Bound, Best Upper Bound, Total Gap(%) ");
                PrintIterationInfo($"#{iter},{ Math.Round(templowerBound) },{ Math.Round(tempUpperbound) },{ Math.Round(lowerBound)}" +
                    $",{ Math.Round(upperBound) },{ gapStr }");

                string ss = "###,";
                foreach(var s in LM_rho)
                {
                   ss+= ($"{s.Key.ToString()}:{ s.Value.ToString() },");
                }
                PrintIterationInfo(ss);

                PrintIterationInfo($"#{iter},{ Math.Round(templowerBound) },{ Math.Round(tempUpperbound) },{ Math.Round(lowerBound)}" +
                    $",{ Math.Round(upperBound) },{ gapStr }");

                if (hasFeasibleSolution && hasBetterUpperbound)
                {
                    ObjValue = pathcost.Sum(i => i.Value);
                    PrintSolution(
                        DpnAlgorithm.GetTravelPathString(_ctx, adapter, solutiongraph, x_upperbound, pathcost),
                        DpnAlgorithm.GetPricingPathString(_ctx, adapter, w));
                }

                #endregion

                Log($"--------------第{iter}轮求解结束--------------");
            }
        }
    }
}