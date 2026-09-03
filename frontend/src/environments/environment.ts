export const environment = {
  production: true,
  // Section 5 architecture: Angular talks to the ASP.NET Core API through Nginx in
  // production (docker-compose service "nginx" on port 8080) rather than hitting
  // ccaas-api directly.
  apiBaseUrl: '/api',
  hubBaseUrl: '/hubs'
};
