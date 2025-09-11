// functions/src/index.ts
import {setGlobalOptions} from "firebase-functions/v2/options";
import {onRequest} from "firebase-functions/v2/https";
import * as logger from "firebase-functions/logger";

setGlobalOptions({region: "us-central1", maxInstances: 10});

// экспорт нашей функции из отдельного файла
// export {markEmailVerified} from "./auth/markEmailVerified";
//export {updateVerifiedOnSignIn} from "./auth/updateVerifiedOnSignIn";


// опционально: простая HTTP-проверка
export const ping = onRequest((req, res) => {
  logger.info("ping");
  res.json({ok: true, ts: Date.now()});
});
