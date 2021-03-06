﻿using Natasha.CSharp;
using Natasha.Excel;
using NPOI.SS.UserModel;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.XPath;

public class ExcelOperator
{

    /// <summary>
    /// 通过属性的注释文本，通过 xml 读取
    /// </summary>
    /// <param name="type"></param>
    /// <returns>Dict：key=属性名，value=注释</returns>
    public static Dictionary<string, string> GetProperySummaryFromXml(string xmlPath, Type type)
    {
        var dic = new Dictionary<string, string>();
        var sReader = new StringReader(File.ReadAllText(xmlPath));
        using (var xmlReader = XmlReader.Create(sReader))
        {
            XPathDocument xpath = null;
            try
            {
                xpath = new XPathDocument(xmlReader);
            }
            catch
            {
                return null;
            }
            var xmlNav = xpath.CreateNavigator();

            var className = (type.IsNested ? $"{type.Namespace}.{type.DeclaringType.Name}.{type.Name}" : $"{type.Namespace}.{type.Name}").Trim('.');
            var node = xmlNav.SelectSingleNode($"/doc/members/member[@name='T:{className}']/summary");

            var props = type.GetProperties();
            foreach (var prop in props)
            {
                className = (prop.DeclaringType.IsNested ? $"{prop.DeclaringType.Namespace}.{prop.DeclaringType.DeclaringType.Name}.{prop.DeclaringType.Name}" : $"{prop.DeclaringType.Namespace}.{prop.DeclaringType.Name}").Trim('.');
                node = xmlNav.SelectSingleNode($"/doc/members/member[@name='P:{className}.{prop.Name}']/summary");
                if (node == null) continue;
                var comment = node.InnerXml.Trim(' ', '\r', '\n', '\t');
                if (string.IsNullOrEmpty(comment)) continue;
                if (prop.Name != default && prop.Name != "")
                {
                    dic.Add(prop.Name, comment);
                }

            }
        }

        return dic;
    }

    /// <summary>
    /// 设置写入映射
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    /// <param name="mappers"></param>
    /// <param name="ignores"></param>
    public static void SetWritterMapping<TEntity>(Dictionary<string, string> mappers, params string[] ignores)
    {
        ExcelOperator<TEntity>.CreateWriteDelegate(mappers, ignores);
    }

    /// <summary>
    /// 设置读取映射
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    /// <param name="mappers"></param>
    /// <param name="ignores"></param>
    public static void SetReaderMapping<TEntity>(Dictionary<string, string> mappers, params string[] ignores)
    {
        ExcelOperator<TEntity>.CreateReadDelegate(mappers, ignores);
    }

    /// <summary>
    /// 将实体写入文件
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    /// <param name="filePath"></param>
    /// <param name="entities"></param>
    /// <param name="sheetPage"></param>
    public static void WriteToFile<TEntity>(string filePath, IEnumerable<TEntity> entities, int sheetPage = 0)
    {
        ExcelOperator<TEntity>.WriteToFile(filePath, entities, sheetPage);
    }

    /// <summary>
    /// 将文件内容写入实体
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    /// <param name="filePath"></param>
    /// <param name="sheetPage"></param>
    /// <returns></returns>
    public static IEnumerable<TEntity> FileToEntities<TEntity>(string filePath, int sheetPage = 0)
    {
        return ExcelOperator<TEntity>.FileToEntities(filePath, sheetPage);
    }
}


public class ExcelOperator<TEntity>
{

    private static ImmutableDictionary<string, string> _mappers;
    private static ImmutableDictionary<string, string> _reverser_mappers;
    private static ImmutableDictionary<string, int> _fields;
    private static Action<ISheet, IEnumerable<TEntity>> Writter;
    private static Func<ISheet, int[], IEnumerable<TEntity>> Reader;

    public static Action<ISheet, IEnumerable<TEntity>> CreateWriteDelegate(Dictionary<string, string> mappers, params string[] ignores)
    {
        _mappers = ImmutableDictionary.CreateRange(mappers);

        HashSet<string> ignorSets = new HashSet<string>(ignores);
        StringBuilder excelBody = new StringBuilder();
        StringBuilder excelHeader = new StringBuilder();
        excelHeader.AppendLine("var rowIndex = 0;");
        excelHeader.AppendLine("IRow row = arg1.CreateRow(rowIndex);");

        excelBody.AppendLine(@"foreach(var item in arg2){");
        excelBody.AppendLine($"rowIndex+=1;");
        excelBody.AppendLine($"row = arg1.CreateRow(rowIndex);");
        int column = 0;
        foreach (var item in mappers)
        {

            if (!ignorSets.Contains(item.Value))
            {

                excelHeader.AppendLine($"row.CreateCell({column}).SetCellValue(\"{item.Value}\");");

                var prop = typeof(TEntity).GetProperty(item.Key);
                if (prop.PropertyType == typeof(string))
                {
                    excelBody.AppendLine($"row.CreateCell({column}).SetCellValue(item.{item.Key});");
                }
                else if (prop.PropertyType.IsGenericType)
                {
                    excelBody.AppendLine($"row.CreateCell({column}).SetCellValue(item.{item.Key}.ToString());");
                }
                else
                {
                    excelBody.AppendLine($"row.CreateCell({column}).SetCellValue(item.{item.Key});");
                }

                column += 1;

            }

        }
        excelBody.AppendLine("}");
        excelHeader.Append(excelBody);
        return Writter = NDelegate
            .UseDomain(typeof(TEntity).GetDomain())
            .Action<ISheet, IEnumerable<TEntity>>(excelHeader.ToString());

    }
    public static Func<ISheet, int[], IEnumerable<TEntity>> CreateReadDelegate(Dictionary<string, string> mappers, params string[] ignores)
    {


        //给字段排序
        int index = 0;
        var tempDict = new Dictionary<string, int>();
        HashSet<string> ignorSets = new HashSet<string>(ignores);

        foreach (var item in mappers)
        {

            if (!ignorSets.Contains(item.Key))
            {
                tempDict[item.Key] = index;
                index += 1;
            }

        }
        _fields = ImmutableDictionary.CreateRange(tempDict);

        Dictionary<string, string> dict = new Dictionary<string, string>();
        foreach (var item in mappers)
        {

            if (!ignorSets.Contains(item.Key))
            {
                dict[item.Value] = item.Key;
            }

        }
        _reverser_mappers = dict.ToImmutableDictionary();


        StringBuilder excelBody = new StringBuilder();
        excelBody.AppendLine($"var list = new List<{typeof(TEntity).GetDevelopName()}>(arg1.LastRowNum);");
        excelBody.AppendLine(@"for(int i = 1;i<=arg1.LastRowNum;i+=1){");
        excelBody.AppendLine("var row = arg1.GetRow(i);");
        excelBody.AppendLine($"var tempCell = row.GetCell(0);");
        excelBody.AppendLine($"var instance = new {typeof(TEntity).GetDevelopName()}();");
        foreach (var item in _fields)
        {
            var prop = typeof(TEntity).GetProperty(item.Key);
            if (prop != null)
            {
                if (prop.PropertyType == typeof(string))
                {
                    excelBody.AppendLine($"instance.{item.Key} = row.GetCell(arg2[{item.Value}]).StringCellValue;");
                }
                else if (prop.PropertyType == typeof(DateTime))
                {
                    excelBody.AppendLine($"instance.{item.Key} = row.GetCell(arg2[{item.Value}]).DateCellValue;");
                }
                else if (prop.PropertyType == typeof(bool))
                {
                    excelBody.AppendLine($"instance.{item.Key} = row.GetCell(arg2[{item.Value}]).BooleanCellValue;");

                }
                else if (prop.PropertyType == typeof(double))
                {
                    excelBody.AppendLine($"instance.{item.Key} = row.GetCell(arg2[{item.Value}]).NumericCellValue;");
                }
                else
                {

                    if (prop.PropertyType.IsGenericType)
                    {
                        excelBody.AppendLine($"tempCell = row.GetCell(arg2[{item.Value}]);");
                        excelBody.AppendLine($"if(tempCell == null){{ instance.{item.Key} = null; }}");
                        excelBody.AppendLine("else{");
                        var eleType = prop.PropertyType.GetGenericArguments()[0];
                        if (eleType == typeof(string))
                        {
                            excelBody.AppendLine($"instance.{item.Key} = tempCell.StringCellValue;");
                        }
                        else if (eleType == typeof(DateTime))
                        {
                            excelBody.AppendLine($"instance.{item.Key} = tempCell.DateCellValue;");
                        }
                        else if (eleType == typeof(bool))
                        {
                            excelBody.AppendLine($"instance.{item.Key} = tempCell.BooleanCellValue;");

                        }
                        else if (eleType == typeof(double))
                        {
                            excelBody.AppendLine($"instance.{item.Key} = tempCell.NumericCellValue;");
                        }
                        else
                        {
                            excelBody.AppendLine($"instance.{item.Key} = Convert.To{eleType.Name}(tempCell.NumericCellValue);");
                        }
                        excelBody.Append('}');
                    }
                    else
                    {
                        excelBody.AppendLine($"instance.{item.Key} = Convert.To{prop.PropertyType.Name}(row.GetCell(arg2[{item.Value}]).NumericCellValue);");
                    }

                }
            }

        }

        excelBody.AppendLine("list.Add(instance);");
        excelBody.AppendLine("}");
        excelBody.AppendLine("return list;");
        return Reader = NDelegate
            .UseDomain(typeof(TEntity).GetDomain(), item => item.LogSyntaxError().LogCompilerError())
            .Func<ISheet, int[], IEnumerable<TEntity>>(excelBody.ToString());
    }


    public static void WriteToFile(string filePath, IEnumerable<TEntity> entities, int sheetPage)
    {

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
        using (var builder = new ExcelBuilder(filePath))
        {
            Writter(builder[sheetPage], entities);
            builder.Save();
        }

    }

    //public static (ISheet,int[]) Get(string filePath, int sheetPage)
    //{

    //    using (var builder = new ExcelBuilder(filePath))
    //    {

    //        var indexs = new int[_mappers.Count];
    //        var sheet = builder[sheetPage];
    //        var row = sheet.GetRow(0);
    //        for (int i = 0; i < row.LastCellNum; i += 1)
    //        {

    //            if (_reverser_mappers.TryGetValue(row.GetCell(i).StringCellValue, out var field))
    //            {
    //                if (_fields.TryGetValue(field, out var value))
    //                {
    //                    indexs[value] = i;
    //                }
    //            }

    //        }
    //        return (sheet, indexs);

    //    }

    //}

    public static IEnumerable<TEntity> FileToEntities(string filePath, int sheetPage)
    {

        using (var builder = new ExcelBuilder(filePath))
        {

            var indexs = new int[_mappers.Count];
            var sheet = builder[sheetPage];
            var row = sheet.GetRow(0);
            for (int i = 0; i < row.LastCellNum; i += 1)
            {

                if (_reverser_mappers.TryGetValue(row.GetCell(i).StringCellValue, out var field))
                {
                    if (_fields.TryGetValue(field, out var value))
                    {
                        indexs[value] = i;
                    }
                }

            }
            return Reader(sheet, indexs);

        }

    }

}


