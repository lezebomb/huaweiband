package dev.bandkey.bridge;

import com.huawei.wearengine.WearEngineException;

final class WearErrors {
    private WearErrors(){}
    static String describe(Throwable error){
        if(error instanceof WearEngineException){
            int code=((WearEngineException)error).getErrorCode();
            String hint;
            switch(code){
                case 2: hint="未安装运动健康"; break;
                case 3: hint="运动健康尚未登录"; break;
                case 4: hint="运动健康未绑定设备"; break;
                case 5: hint="接口参数无效"; break;
                case 8: hint="应用尚未获准此 Wear Engine 服务范围"; break;
                case 9: hint="用户尚未授予 Wear Engine 权限"; break;
                case 12: hint="华为服务内部错误；不能据此判断设备不支持"; break;
                case 13: hint="设备版本不支持"; break;
                case 16: hint="手环尚未连接"; break;
                case 18: hint="设备或服务不支持此接口"; break;
                case 19: hint="调用超时"; break;
                default: hint="查看华为错误码文档";
            }
            return "WearEngineException code="+code+"（"+hint+"）";
        }
        return error.getClass().getSimpleName();
    }
}
