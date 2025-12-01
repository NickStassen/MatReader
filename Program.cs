using System;
using System.IO;
using MatFileHandler;

class Program
{
    static void Main(string[] args)
    {
        string matDir = args.Length > 0 ? args[0] : ".";
        string outputDir = args.Length > 1 ? args[1] : "./output";
        bool verbose = args.Length > 2 && args[2] == "-v";
        
        Directory.CreateDirectory(outputDir);
        
        var matFiles = Directory.GetFiles(matDir, "*.mat");
        Console.WriteLine($"Found {matFiles.Length} .mat files");
        
        int successCount = 0;
        foreach (var matPath in matFiles)
        {
            try
            {
                if (ProcessMatFile(matPath, outputDir, verbose))
                    successCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR {Path.GetFileName(matPath)}: {ex.Message}");
                if (verbose) Console.WriteLine(ex.StackTrace);
            }
        }
        Console.WriteLine($"\nProcessed {successCount}/{matFiles.Length} files successfully");
    }
    
    static bool ProcessMatFile(string matPath, string outputDir, bool verbose)
    {
        string fileName = Path.GetFileNameWithoutExtension(matPath);
        
        IMatFile matFile;
        using (var fs = new FileStream(matPath, FileMode.Open, FileAccess.Read))
        {
            var reader = new MatFileReader(fs);
            matFile = reader.Read();
        }
        
        var gTruth = matFile["gTruth"]?.Value;
        if (gTruth == null) { Console.WriteLine($"{fileName}: No gTruth variable"); return false; }
        
        // The structure is: gTruth (IMatObject) -> "any" field -> IStructureArray with DataSource/LabelData/etc
        if (verbose)
        {
            Console.WriteLine($"\n=== {fileName} ===");
            Console.WriteLine($"gTruth type: {gTruth.GetType().Name}");
            ExploreAny(gTruth, "  ", 0);
        }
        
        // Try to navigate the structure
        IArray? labelDataRaw = NavigateToField(gTruth, "LabelData");
        IArray? labelDefsRaw = NavigateToField(gTruth, "LabelDefinitions");
        IArray? dataSourceRaw = NavigateToField(gTruth, "DataSource");
        
        if (labelDataRaw == null)
        {
            Console.WriteLine($"{fileName}: Could not find LabelData");
            return false;
        }
        
        // Extract video path if available
        string videoPath = "";
        if (dataSourceRaw != null)
        {
            videoPath = ExtractString(NavigateToField(dataSourceRaw, "Source")) ?? "";
        }
        
        // Extract class names from LabelDefinitions
        var classNames = new List<string>();
        if (labelDefsRaw != null)
        {
            var nameField = NavigateToField(labelDefsRaw, "Name");
            if (nameField is ICellArray nameCells)
            {
                for (int i = 0; i < nameCells.Count; i++)
                {
                    var s = ExtractString(nameCells[i]);
                    if (s != null) classNames.Add(s);
                }
            }
            else
            {
                var s = ExtractString(nameField);
                if (s != null) classNames.Add(s);
            }
        }
        
        if (verbose)
        {
            Console.WriteLine($"  Video: {videoPath}");
            Console.WriteLine($"  Classes: {string.Join(", ", classNames)}");
            Console.WriteLine($"  LabelData type: {labelDataRaw.GetType().Name}");
        }
        
        // Now extract bboxes from LabelData
        // LabelData should be a timetable/table with rows=frames, columns=class labels
        var bboxes = new List<(int frame, string label, double x, double y, double w, double h)>();
        
        ExtractBboxes(labelDataRaw, classNames, bboxes, verbose);
        
        if (bboxes.Count == 0)
        {
            Console.WriteLine($"{fileName}: No bboxes extracted");
            return false;
        }
        
        // Write CSV
        string csvPath = Path.Combine(outputDir, $"{fileName}.csv");
        using (var sw = new StreamWriter(csvPath))
        {
            sw.WriteLine("frame,label,x,y,width,height");
            foreach (var (frame, label, x, y, w, h) in bboxes)
            {
                sw.WriteLine($"{frame},{label},{x:F2},{y:F2},{w:F2},{h:F2}");
            }
        }
        
        Console.WriteLine($"{fileName}: {bboxes.Count} bboxes -> {csvPath}");
        return true;
    }
    
    static IArray? NavigateToField(IArray arr, string fieldName)
    {
        // Handle IMatObject
        if (arr is IMatObject matObj)
        {
            // First check if field exists directly
            if (matObj.FieldNames.Contains(fieldName))
            {
                try { return matObj[fieldName, 0]; } catch { }
            }
            
            // Check "any" field (common wrapper)
            if (matObj.FieldNames.Contains("any"))
            {
                var anyField = matObj["any", 0];
                if (anyField != null)
                    return NavigateToField(anyField, fieldName);
            }
            
            // Try first field
            foreach (var fn in matObj.FieldNames)
            {
                var inner = matObj[fn, 0];
                if (inner != null)
                {
                    var result = NavigateToField(inner, fieldName);
                    if (result != null) return result;
                }
            }
        }
        
        // Handle IStructureArray
        if (arr is IStructureArray structArr)
        {
            if (structArr.FieldNames.Contains(fieldName))
            {
                try { return structArr[fieldName, 0]; } catch { }
            }
            
            // Recurse into nested structures
            foreach (var fn in structArr.FieldNames)
            {
                try
                {
                    var inner = structArr[fn, 0];
                    if (inner != null)
                    {
                        var result = NavigateToField(inner, fieldName);
                        if (result != null) return result;
                    }
                }
                catch { }
            }
        }
        
        // Handle ICellArray
        if (arr is ICellArray cellArr && cellArr.Count > 0)
        {
            var first = cellArr[0];
            if (first != null)
                return NavigateToField(first, fieldName);
        }
        
        return null;
    }
    
    static string? ExtractString(IArray? arr)
    {
        if (arr == null) return null;
        if (arr is ICharArray ca) return ca.String;
        if (arr is ICellArray cell && cell.Count > 0)
            return ExtractString(cell[0]);
        return null;
    }
    
    static void ExtractBboxes(IArray labelData, List<string> classNames, 
        List<(int, string, double, double, double, double)> bboxes, bool verbose)
    {
        // Try as structure array first
        if (labelData is IStructureArray structArr)
        {
            if (verbose) Console.WriteLine($"  LabelData fields: {string.Join(", ", structArr.FieldNames)}");
            
            // Each field (except Time/Properties) should be a column of bbox data
            foreach (var fieldName in structArr.FieldNames)
            {
                if (fieldName == "Time" || fieldName == "Properties" || fieldName == "rowDim" || fieldName == "varDim")
                    continue;
                
                // This field contains bbox data for one class
                for (int frameIdx = 0; frameIdx < structArr.Count; frameIdx++)
                {
                    try
                    {
                        var cell = structArr[fieldName, frameIdx];
                        ExtractBboxFromCell(cell, frameIdx, fieldName, bboxes);
                    }
                    catch { }
                }
            }
        }
        
        // Try as MatObject with nested structure
        if (labelData is IMatObject matObj)
        {
            foreach (var fn in matObj.FieldNames)
            {
                var inner = matObj[fn, 0];
                if (inner != null)
                    ExtractBboxes(inner, classNames, bboxes, verbose);
            }
        }
        
        // Try TableAdapter if nothing else worked
        if (bboxes.Count == 0)
        {
            try
            {
                var table = new TableAdapter(labelData);
                if (verbose) Console.WriteLine($"  TableAdapter: {table.NumberOfRows} rows, vars: {string.Join(", ", table.VariableNames)}");
                
                foreach (var varName in table.VariableNames)
                {
                    var col = table[varName];
                    if (col is ICellArray cellCol)
                    {
                        for (int row = 0; row < Math.Min(table.NumberOfRows, cellCol.Count); row++)
                        {
                            ExtractBboxFromCell(cellCol[row], row, varName, bboxes);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (verbose) Console.WriteLine($"  TableAdapter failed: {ex.Message}");
            }
        }
    }
    
    static void ExtractBboxFromCell(IArray? cell, int frameIdx, string label, 
        List<(int, string, double, double, double, double)> bboxes)
    {
        if (cell == null || cell.IsEmpty) return;
        
        double[]? data = null;
        
        // Unwrap cell arrays
        if (cell is ICellArray ca)
        {
            if (ca.Count > 0)
                data = ca[0]?.ConvertToDoubleArray();
        }
        else
        {
            data = cell.ConvertToDoubleArray();
        }
        
        if (data == null || data.Length < 4) return;
        
        // Handle multiple bboxes per frame (data.Length / 4 bboxes)
        // MATLAB stores column-major, so for N bboxes we have:
        // [x1,x2,...,xN, y1,y2,...,yN, w1,...,wN, h1,...,hN]
        int numBboxes = data.Length / 4;
        
        if (numBboxes == 1)
        {
            bboxes.Add((frameIdx, label, data[0], data[1], data[2], data[3]));
        }
        else
        {
            // Column-major layout
            for (int i = 0; i < numBboxes; i++)
            {
                double x = data[i];
                double y = data[numBboxes + i];
                double w = data[2 * numBboxes + i];
                double h = data[3 * numBboxes + i];
                bboxes.Add((frameIdx, label, x, y, w, h));
            }
        }
    }
    
    static void ExploreAny(IArray arr, string indent, int depth)
    {
        if (depth > 4) return;
        
        if (arr is IMatObject mo)
        {
            Console.WriteLine($"{indent}MatObject class={mo.ClassName}, fields=[{string.Join(", ", mo.FieldNames)}]");
            foreach (var fn in mo.FieldNames)
            {
                try
                {
                    var f = mo[fn, 0];
                    if (f != null)
                    {
                        Console.WriteLine($"{indent}  .{fn}:");
                        ExploreAny(f, indent + "    ", depth + 1);
                    }
                }
                catch { }
            }
        }
        else if (arr is IStructureArray sa)
        {
            Console.WriteLine($"{indent}StructArray count={sa.Count}, fields=[{string.Join(", ", sa.FieldNames)}]");
            foreach (var fn in sa.FieldNames)
            {
                try
                {
                    var f = sa[fn, 0];
                    if (f != null)
                    {
                        Console.WriteLine($"{indent}  .{fn}: {f.GetType().Name}");
                        if (f is ICharArray ca)
                            Console.WriteLine($"{indent}    = \"{ca.String}\"");
                        else if (depth < 3)
                            ExploreAny(f, indent + "    ", depth + 1);
                    }
                }
                catch { }
            }
        }
        else if (arr is ICellArray ca)
        {
            Console.WriteLine($"{indent}CellArray count={ca.Count}");
            for (int i = 0; i < Math.Min(ca.Count, 2); i++)
            {
                var c = ca[i];
                if (c != null)
                {
                    Console.WriteLine($"{indent}  [{i}]:");
                    ExploreAny(c, indent + "    ", depth + 1);
                }
            }
        }
        else if (arr is ICharArray cha)
        {
            Console.WriteLine($"{indent}CharArray = \"{cha.String}\"");
        }
        else
        {
            var d = arr.ConvertToDoubleArray();
            if (d != null && d.Length <= 8)
                Console.WriteLine($"{indent}{arr.GetType().Name} = [{string.Join(", ", d)}]");
            else
                Console.WriteLine($"{indent}{arr.GetType().Name} dims=[{string.Join(",", arr.Dimensions)}]");
        }
    }
}
