using IoTGateway.Model;
using IoTGateway.ViewModel.BasicData.DeviceVMs;
using Microsoft.AspNetCore.Mvc;
using Plugin;
using System;
using System.Collections.Generic;
using System.Linq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Mvc;



namespace IoTGateway.Controllers
{
    [Area("BasicData")]
    [AuthorizeJwtWithCookie]
    [ActionDescription("设备维护Api")]
    [ApiController]
    [Route("api/Device")]
    public partial class DeviceApiController : BaseApiController
    {

        private readonly DeviceService _deviceService;

        public DeviceApiController(DeviceService deviceService)
        {
            _deviceService = deviceService;
        }

        [ActionDescription("Sys.Search")]
        [HttpPost("Search")]
        public IActionResult Search(DeviceApiSearcher searcher)
        {
            if (ModelState.IsValid)
            {
                var vm = Wtm.CreateVM<DeviceApiListVM>(passInit: true);
                vm.Searcher = searcher;
                return Content(vm.GetJson());
            }
            else
            {
                return BadRequest(ModelState.GetErrorJson());
            }
        }

        [ActionDescription("Sys.Get")]
        [HttpGet("{id}")]
        public DeviceApiVM Get(string id)
        {
            var vm = Wtm.CreateVM<DeviceApiVM>(id);
            return vm;
        }

        [ActionDescription("Sys.Create")]
        [HttpPost("Add")]
        public IActionResult Add(DeviceApiVM vm)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState.GetErrorJson());
            }
            else
            {
                vm.DoAdd();
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState.GetErrorJson());
                }
                else
                {
                    return Ok(vm.Entity);
                }
            }
        }

        [ActionDescription("Sys.Edit")]
        [HttpPut("Edit")]
        public IActionResult Edit(DeviceApiVM vm)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState.GetErrorJson());
            }
            else
            {
                vm.DoEdit(false);
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState.GetErrorJson());
                }
                else
                {
                    return Ok(vm.Entity);
                }
            }
        }

        [HttpPost("BatchDelete")]
        [ActionDescription("Sys.Delete")]
        public IActionResult BatchDelete(string[] ids)
        {
            var vm = Wtm.CreateVM<DeviceApiBatchVM>();
            if (ids != null && ids.Count() > 0)
            {
                vm.Ids = ids;
            }
            else
            {
                return Ok();
            }
            if (!ModelState.IsValid || !vm.DoBatchDelete())
            {
                return BadRequest(ModelState.GetErrorJson());
            }
            else
            {
                return Ok(ids.Count());
            }
        }

        [ActionDescription("Sys.Export")]
        [HttpPost("ExportExcel")]
        public IActionResult ExportExcel(DeviceApiSearcher searcher)
        {
            var vm = Wtm.CreateVM<DeviceApiListVM>();
            vm.Searcher = searcher;
            vm.SearcherMode = ListVMSearchModeEnum.Export;
            return vm.GetExportData();
        }

        [ActionDescription("Sys.CheckExport")]
        [HttpPost("ExportExcelByIds")]
        public IActionResult ExportExcelByIds(string[] ids)
        {
            var vm = Wtm.CreateVM<DeviceApiListVM>();
            if (ids != null && ids.Count() > 0)
            {
                vm.Ids = new List<string>(ids);
                vm.SearcherMode = ListVMSearchModeEnum.CheckExport;
            }
            return vm.GetExportData();
        }

        [ActionDescription("Sys.DownloadTemplate")]
        [HttpGet("GetExcelTemplate")]
        public IActionResult GetExcelTemplate()
        {
            var vm = Wtm.CreateVM<DeviceApiImportVM>();
            var qs = new Dictionary<string, string>();
            foreach (var item in Request.Query.Keys)
            {
                qs.Add(item, Request.Query[item]);
            }
            vm.SetParms(qs);
            var data = vm.GenerateTemplate(out string fileName);
            return File(data, "application/vnd.ms-excel", fileName);
        }

        [ActionDescription("Sys.Import")]
        [HttpPost("Import")]
        public ActionResult Import(DeviceApiImportVM vm)
        {
            if (vm != null && (vm.ErrorListVM.EntityList.Count > 0 || !vm.BatchSaveData()))
            {
                return BadRequest(vm.GetErrorJson());
            }
            else
            {
                return Ok(vm?.EntityList?.Count ?? 0);
            }
        }

        [HttpGet("GetDrivers")]
        public ActionResult GetDrivers()
        {
            return Ok(DC.Set<Driver>().GetSelectListItems(Wtm, x => x.DriverName));
        }

        [HttpGet("GetDevices")]
        public ActionResult GetDevices()
        {
            return Ok(DC.Set<Device>().GetSelectListItems(Wtm, x => x.DeviceName));
        }


        /// <summary>
        /// 获取单个变量当前值（公开接口，不需要登录）
        /// </summary>
        /// <param name="deviceName">设备名称（配置中的 DeviceName）</param>
        /// <param name="variable">变量名称（配置中的 Name 字段，而不是序号 0/1/2）</param>
        [Public]               // 关键：标记为公开接口，绕过 AuthorizeJwtWithCookie
        [HttpGet("GetVariable")]
        public IActionResult GetVariable(string deviceName, string variable)
        {
            if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(variable))
            {
                return BadRequest("deviceName 和 variable 不能为空");
            }

            // 1. 找设备线程
            var thread = _deviceService.DeviceThreads
                .FirstOrDefault(x => x.Device.DeviceName == deviceName);

            if (thread == null)
            {
                return NotFound($"找不到设备: {deviceName}");
            }

            // 2. 在该设备下找变量
            var dv = thread.Device.DeviceVariables
                .FirstOrDefault(x => x.Name == variable);

            if (dv == null)
            {
                return NotFound($"设备 {deviceName} 中不存在变量 {variable}");
            }

            // 3. 返回当前值（你也可以只返回 CookedValue，看自己需要）
            return Ok(new
            {
                Device = deviceName,
                Variable = variable,
                Value = dv.CookedValue,  // 解析后的值，一般拿这个
                Raw = dv.Value,          // 原始值（如果有）
               // Status = dv.Status,      // 变量状态（如果有这个字段）
            });
        }

    }
}