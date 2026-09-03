export const environment = {
  production: false,
  // `ng serve` runs on its own dev-server port, so point straight at the API container's
  // published port (docker-compose maps ccaas-api's 8080 to host 5000) instead of Nginx.
  apiBaseUrl: 'http://localhost:5000/api',
  hubBaseUrl: 'http://localhost:5000/hubs'
};
